using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
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

        // Ausente só sem nenhum valor: vários valores (mesmo com o primeiro vazio) caem na validação de valor único
        var values = context.Request.Headers[_options.HeaderName];
        if (values.Count == 0 || (values.Count == 1 && string.IsNullOrEmpty(values[0])))
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
            bool authenticated = context.User.Identity?.IsAuthenticated == true;
            if (metadata.KeyRequired)
            {
                // Chave obrigatória: executar sem reserva seria executar sem a proteção que o endpoint exige
                await WriteMissingScopeAsync(context, authenticated).ConfigureAwait(false);
                return;
            }

            if (authenticated)
                IdempotencyLog.UserWithoutIdClaim(logger);
            else
                IdempotencyLog.AnonymousRequest(logger);
            await next(context).ConfigureAwait(false);
            return;
        }

        string? hash = await ComputeHashAsync(context.Request, _options.MaxRequestBodyBytes, _options.HashedRequestHeaders,
            context.RequestAborted).ConfigureAwait(false);
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

    /// <summary>
    /// Executa o endpoint com a resposta em buffer (nada vai ao cliente durante a execução), guarda a resposta 2xx e só
    /// então a envia: a entrega ao cliente (que pode ter desconectado) não decide se a chave é liberada.
    /// </summary>
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
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested
                                                 && context.Response.StatusCode is >= 200 and <= 299)
        {
            // O cliente desconectou e a operação pode ter sido confirmada: a reserva é mantida (a repetição recebe 409 até o
            // LockTimeout), em vez de liberar a chave e arriscar executar de novo
            IdempotencyLog.AbortedAfterSuccess(logger);
            throw;
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
            await capture.SendBufferedAsync().ConfigureAwait(false);
            return;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _options.StoredResponseHeaders)
        {
            if (context.Response.Headers.TryGetValue(name, out var header) && header.Count == 1 && !string.IsNullOrEmpty(header[0]))
                headers[name] = header[0]!;
        }

        // Guarda antes de enviar: se o envio falhar (cliente desconectado), a repetição recebe a resposta guardada
        var response = new IdempotentResponse(status, context.Response.ContentType, headers, capture.Captured);
        if (!await store.CompleteAsync(key, lockId, response, CancellationToken.None).ConfigureAwait(false))
            IdempotencyLog.LockLost(logger);

        await capture.SendBufferedAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Escopo da chave: SHA-256 do tipo, do emissor e do valor do primeiro claim de <see cref="IdempotencyOptions.UserIdClaimTypes"/>
    /// encontrado numa identidade autenticada, mais o <see cref="IdempotencyOptions.ScopeDiscriminator"/> (ex.: tenant);
    /// <c>null</c> sem usuário identificado.
    /// </summary>
    /// <remarks>
    /// Com o tipo e o emissor, valores iguais de esquemas de autenticação diferentes não compartilham as chaves. O hash tem
    /// tamanho fixo (cabe em <see cref="IdempotencyKey.MaxScopeLength"/>) e não grava o id do usuário no store.
    /// </remarks>
    private string? ResolveScope(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;

        foreach (var type in _options.UserIdClaimTypes)
        {
            // Só identidades autenticadas: uma identidade anexada sem autenticação não pode escolher o escopo
            var claim = FindAuthenticatedClaim(context.User, type);
            if (claim is null)
                continue;

            string? discriminator = _options.ScopeDiscriminator?.Invoke(context);
            return HashFields(type, claim.Issuer, claim.Value, discriminator ?? "");
        }

        return null;
    }

    private static Claim? FindAuthenticatedClaim(ClaimsPrincipal user, string type)
    {
        foreach (var identity in user.Identities)
        {
            if (identity.IsAuthenticated && identity.FindFirst(type) is { } claim && !string.IsNullOrWhiteSpace(claim.Value))
                return claim;
        }

        return null;
    }

    /// <summary>SHA-256 (hexadecimal) dos campos, cada um prefixado pelo tamanho (sem ambiguidade na concatenação).</summary>
    private static string HashFields(params ReadOnlySpan<string> fields)
    {
        var text = new StringBuilder();
        foreach (var field in fields)
            text.Append(field.Length).Append(':').Append(field).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// SHA-256 de método, caminho, query, cabeçalhos de <paramref name="hashedHeaders"/> e corpo; <c>null</c> se o corpo
    /// passar do limite.
    /// </summary>
    internal static async Task<string?> ComputeHashAsync(HttpRequest request, long maxBodyBytes, IEnumerable<string> hashedHeaders,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));

        // Ordem fixa (independe da configuração); ausente e vazio são diferentes
        foreach (var name in hashedHeaders.Order(StringComparer.OrdinalIgnoreCase))
        {
            string header = request.Headers.TryGetValue(name, out var values)
                ? HashFields([name.ToUpperInvariant(), .. values.Select(v => v ?? "")])
                : HashFields(name.ToUpperInvariant());
            hash.AppendData(Encoding.UTF8.GetBytes(header + "\n"));
        }

        if (request.ContentLength > maxBodyBytes)
            return null;

        request.EnableBuffering();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBodyBytes)
                    return null;
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Endpoint com chave obrigatória sem escopo: 401 para o anônimo; 500 (configuração) para usuário sem claim de id.</summary>
    private Task WriteMissingScopeAsync(HttpContext context, bool authenticated)
    {
        if (!authenticated)
            return WriteErrorAsync(context, Error.Unauthorized(UnauthenticatedException.DefaultCode, UnauthenticatedException.DefaultMessage));

        IdempotencyLog.RequiredKeyWithoutScope(logger);
        const string message = "Não foi possível identificar o usuário para a idempotência desta operação.";
        return new ApiResponseHttpResult<ApiResponse>(ApiResponse.Fail(StatusCodes.Status500InternalServerError, message,
            new ApiError("IDEMPOTENCIA_SEM_ESCOPO", message))).ExecuteAsync(context);
    }

    private static Task WriteErrorAsync(HttpContext context, Error error) =>
        new ApiResponseHttpResult<ApiResponse>(ApiResponse.FromResult(Result.Failure(error))).ExecuteAsync(context);
}

/// <summary>
/// Guarda a resposta em memória, sem enviá-la ao cliente, até o limite (<see cref="SendBufferedAsync"/> envia depois). Acima
/// do limite, envia o que estava guardado e passa a repassar direto ao cliente (<see cref="Overflowed"/>: não é guardada).
/// </summary>
internal sealed class CaptureStream(Stream inner, int maxBytes) : Stream
{
    private MemoryStream? _buffer = new();

    /// <summary>A resposta passou do limite e está sendo repassada direto ao cliente.</summary>
    public bool Overflowed => _buffer is null;

    /// <summary>Resposta guardada (sem cópia; vazia se <see cref="Overflowed"/>).</summary>
    public ReadOnlyMemory<byte> Captured => _buffer is null
        ? ReadOnlyMemory<byte>.Empty
        : new ReadOnlyMemory<byte>(_buffer.GetBuffer(), 0, (int)_buffer.Length);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Envia ao cliente a resposta guardada. Sem o <c>RequestAborted</c>: a resposta já está decidida (e guardada, em 2xx);
    /// com a conexão encerrada, o servidor descarta a escrita.
    /// </summary>
    public async Task SendBufferedAsync()
    {
        if (_buffer is { Length: > 0 })
            await inner.WriteAsync(Captured, CancellationToken.None).ConfigureAwait(false);
    }

    // Enquanto guarda, nada é enviado, nem o flush (enviaria os cabeçalhos antes de a resposta ser guardada)
    public override void Flush()
    {
        if (_buffer is null)
            inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _buffer is null ? inner.FlushAsync(cancellationToken) : Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (TryCapture(buffer.AsSpan(offset, count)))
            return;
        if (TakeOverflowed() is { } pending)
            inner.Write(pending.Span);
        inner.Write(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (TryCapture(buffer.Span))
            return;
        if (TakeOverflowed() is { } pending)
            await inner.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Guarda <paramref name="data"/> se ainda couber no limite.</summary>
    private bool TryCapture(ReadOnlySpan<byte> data)
    {
        if (_buffer is null || _buffer.Length + data.Length > maxBytes)
            return false;

        _buffer.Write(data);
        return true;
    }

    /// <summary>Na primeira passagem do limite, devolve o que estava guardado (enviado antes do restante) e passa a repassar.</summary>
    private ReadOnlyMemory<byte>? TakeOverflowed()
    {
        if (_buffer is null)
            return null;

        var pending = Captured.ToArray();
        _buffer.Dispose();
        _buffer = null;
        return pending;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _buffer?.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _buffer?.Dispose();
        return base.DisposeAsync();
    }
}
