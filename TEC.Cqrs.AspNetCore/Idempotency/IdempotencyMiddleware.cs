using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Core.Common.Results;
using TEC.Core.Responses;
using TEC.Cqrs.AspNetCore.Internal;
using TEC.Cqrs.Idempotency;

namespace TEC.Cqrs.AspNetCore.Idempotency;

/// <summary>
/// Idempotência por <c>Idempotency-Key</c> nos endpoints marcados com <see cref="IdempotentAttribute"/>: reserva a chave
/// antes de executar (requisições concorrentes com a mesma chave não executam de novo), guarda as respostas 2xx e as repete.
/// </summary>
/// <remarks>O store é resolvido por requisição (pode ser Scoped, como o do TEC.ORM, que usa o DbContext).</remarks>
internal sealed class IdempotencyMiddleware(RequestDelegate next, IOptions<IdempotencyOptions> options, ILogger<IdempotencyMiddleware> logger)
{
    private readonly IdempotencyOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        var metadata = context.GetEndpoint()?.Metadata.GetMetadata<IdempotentAttribute>();
        if (metadata is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var values = context.Request.Headers[_options.HeaderName];
        if (values.Count == 0 || string.IsNullOrEmpty(values[0]))
        {
            if (metadata.KeyRequired)
            {
                await WriteErrorAsync(context, Error.Validation("IDEMPOTENCY_KEY_OBRIGATORIA",
                    $"Informe o cabeçalho {_options.HeaderName}.", _options.HeaderName)).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        string? value = values.Count == 1 ? values[0] : null;
        if (!IdempotencyKey.IsValid(value, _options.MaxKeyLength))
        {
            await WriteErrorAsync(context, Error.Validation("IDEMPOTENCY_KEY_INVALIDA",
                $"{_options.HeaderName} deve ser um único valor com até {_options.MaxKeyLength} caracteres, sem caracteres de controle.",
                _options.HeaderName)).ConfigureAwait(false);
            return;
        }

        string? scope = ResolveScope(context);
        if (scope is null)
        {
            // Sem usuário identificado não há escopo seguro: a resposta de um anônimo seria repetida para outro
            IdempotencyLog.AnonymousRequest(logger);
            await next(context).ConfigureAwait(false);
            return;
        }

        string? hash = await ComputeHashAsync(context.Request, _options.MaxRequestBodyBytes, context.RequestAborted).ConfigureAwait(false);
        if (hash is null)
        {
            const string message = "O corpo da requisição excede o tamanho máximo aceito em operações idempotentes.";
            await new ApiResponseHttpResult<ApiResponse>(ApiResponse.Fail(StatusCodes.Status413PayloadTooLarge, message,
                new ApiError("IDEMPOTENCIA_CORPO_EXCEDE_LIMITE", message))).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var key = new IdempotencyKey(scope, value!);
        var begin = await store.TryBeginAsync(new IdempotencyRequest(key, hash, _options.Retention, _options.LockTimeout), context.RequestAborted)
            .ConfigureAwait(false);

        switch (begin.Status)
        {
            case IdempotencyBeginStatus.Completed:
                IdempotencyLog.Replayed(logger);
                await ReplayAsync(context, begin.Response!).ConfigureAwait(false);
                return;

            case IdempotencyBeginStatus.InProgress:
                IdempotencyLog.InProgress(logger);
                await WriteErrorAsync(context, Error.Conflict("IDEMPOTENCIA_EM_ANDAMENTO",
                    "Uma requisição com a mesma chave ainda está em processamento. Tente novamente em instantes.")).ConfigureAwait(false);
                return;

            case IdempotencyBeginStatus.Mismatch:
                IdempotencyLog.Mismatch(logger);
                await WriteErrorAsync(context, Error.BusinessRule("IDEMPOTENCY_KEY_REUTILIZADA",
                    $"{_options.HeaderName} já foi usada com outro conteúdo.")).ConfigureAwait(false);
                return;

            default:
                await ExecuteAsync(context, store, key, begin.LockId).ConfigureAwait(false);
                return;
        }
    }

    private async Task ExecuteAsync(HttpContext context, IIdempotencyStore store, IdempotencyKey key, Guid lockId)
    {
        var original = context.Response.Body;
        var capture = new CaptureStream(original, _options.MaxResponseBodyBytes);
        await using var disposeCapture = capture.ConfigureAwait(false);
        context.Response.Body = capture;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            // A chave é liberada: o cliente pode tentar de novo
            await store.AbandonAsync(key, lockId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        var status = context.Response.StatusCode;
        if (status is < 200 or > 299 || capture.Overflowed)
        {
            if (capture.Overflowed && status is >= 200 and <= 299)
                IdempotencyLog.ResponseTooLarge(logger, _options.MaxResponseBodyBytes);
            await store.AbandonAsync(key, lockId, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _options.StoredResponseHeaders)
        {
            if (context.Response.Headers.TryGetValue(name, out var header) && header.Count == 1 && !string.IsNullOrEmpty(header[0]))
                headers[name] = header[0]!;
        }

        var response = new IdempotentResponse(status, context.Response.ContentType, headers, capture.Captured);
        if (!await store.CompleteAsync(key, lockId, response, CancellationToken.None).ConfigureAwait(false))
            IdempotencyLog.LockLost(logger);
    }

    private async Task ReplayAsync(HttpContext context, IdempotentResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        if (response.ContentType is not null)
            context.Response.ContentType = response.ContentType;
        foreach (var (name, value) in response.Headers)
            context.Response.Headers[name] = value;
        context.Response.Headers[_options.ReplayedHeaderName] = "true";
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private string? ResolveScope(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return null;

        foreach (var type in _options.UserIdClaimTypes)
        {
            var id = user.FindFirst(type)?.Value;
            if (IdempotencyKey.IsValid(id, IdempotencyKey.MaxScopeLength))
                return id;
        }

        return null;
    }

    /// <summary>SHA-256 de método, caminho, query e corpo; <c>null</c> se o corpo passar do limite.</summary>
    internal static async Task<string?> ComputeHashAsync(HttpRequest request, long maxBodyBytes, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));

        if (request.ContentLength > maxBodyBytes)
            return null;

        request.EnableBuffering();
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBodyBytes)
                return null;
            hash.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static Task WriteErrorAsync(HttpContext context, Error error) =>
        new ApiResponseHttpResult<ApiResponse>(ApiResponse.FromResult(Result.Failure(error))).ExecuteAsync(context);
}

/// <summary>Repassa a resposta ao cliente e guarda uma cópia até o limite.</summary>
internal sealed class CaptureStream(Stream inner, int maxBytes) : Stream
{
    private readonly MemoryStream _buffer = new();

    public bool Overflowed { get; private set; }

    public ReadOnlyMemory<byte> Captured => Overflowed ? ReadOnlyMemory<byte>.Empty : _buffer.ToArray();

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Capture(ReadOnlySpan<byte> data)
    {
        if (Overflowed)
            return;
        if (_buffer.Length + data.Length > maxBytes)
        {
            Overflowed = true;
            _buffer.SetLength(0);
            return;
        }

        _buffer.Write(data);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _buffer.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _buffer.Dispose();
        return base.DisposeAsync();
    }
}
