using TEC.Core.Common.Results;
using TEC.Core.Responses;
using TEC.Core.Responses.Pagination;

namespace TEC.Cqrs.AspNetCore;

/// <summary>
/// Converte <see cref="Result"/> em resposta HTTP padronizada (<see cref="ApiResponse"/>), com o status definido
/// pelo <see cref="ErrorType"/> do primeiro erro (ou 500/502 se houver algum erro interno na lista). Erros internos
/// (500/502) nunca expõem mensagem ou detalhes.
/// </summary>
/// <example>
/// Minimal API:
/// <code>
/// app.MapPost("/clientes", (CriarClienteCommand command, ISender sender, CancellationToken ct) =>
///     sender.Send(command, ct).ToCreatedHttpResult(id => $"/clientes/{id}"));
///
/// app.MapGet("/clientes/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =>
///     sender.Send(new ObterClienteQuery(id), ct).ToHttpResult());
/// </code>
/// Controller:
/// <code>
/// [HttpGet("{id:guid}")]
/// public async Task&lt;IActionResult&gt; Obter(Guid id, CancellationToken ct) =>
///     await sender.Send(new ObterClienteQuery(id), ct).ToHttpResult();
/// </code>
/// </example>
public static class ResultHttpExtensions
{
    /// <summary>Sucesso: HTTP 200 sem dados. Falha: status e erros conforme o tipo do erro.</summary>
    public static ApiResponseHttpResult<ApiResponse> ToHttpResult(this Result result, string? successMessage = null) =>
        new(ApiResponse.FromResult(result, successMessage));

    /// <summary>Sucesso: HTTP 200 com os dados. Falha: status e erros conforme o tipo do erro.</summary>
    public static ApiResponseHttpResult<ApiResponse<T>> ToHttpResult<T>(this Result<T> result, string? successMessage = null) =>
        new(ApiResponse<T>.FromResult(result, successMessage));

    /// <summary>Sucesso: HTTP 200 com os itens e o bloco <c>pagination</c>. Falha: status e erros conforme o tipo do erro.</summary>
    /// <remarks>Em falha, o envelope não tem <c>data</c> nem <c>pagination</c> (o JSON é o mesmo de qualquer falha).</remarks>
    public static ApiResponseHttpResult<PagedResponse<T>> ToHttpResult<T>(this Result<PagedResult<T>> result,
        string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
            return new(PagedResponse<T>.Create(result.Value, successMessage));

        // O mapeamento de status/erros (e a ocultação de erros internos) é o do TEC.Core
        var failure = ApiResponse<IReadOnlyList<T>>.FromResult(result.ToFailure<IReadOnlyList<T>>());
        return new(new PagedResponse<T>
        {
            Success = false,
            StatusCode = failure.StatusCode,
            Message = failure.Message,
            Errors = failure.Errors,
            Timestamp = failure.Timestamp
        });
    }

    /// <summary>Sucesso: HTTP 201 com os dados e cabeçalho <c>Location</c>. Falha: status e erros conforme o tipo do erro.</summary>
    /// <param name="result">Resultado do command.</param>
    /// <param name="location">
    /// Monta a URL <b>relativa</b> do recurso criado a partir do valor (ex.: <c>id =&gt; $"/clientes/{id}"</c>).
    /// </param>
    /// <param name="successMessage">Mensagem opcional de sucesso.</param>
    /// <remarks>
    /// <para>Caracteres fora do permitido em URL (espaço, acentos etc.) são escapados por segmento:
    /// <c>/clientes/João Silva</c> vira <c>/clientes/Jo%C3%A3o%20Silva</c>. Sequências <c>%XX</c> já escapadas são mantidas.</para>
    /// <para>Somente caminhos relativos iniciados por <c>/</c> são aceitos. URL absoluta, <c>//host</c>, barra invertida ou
    /// caracteres de controle (redirecionamento aberto, injeção de cabeçalho) não geram o cabeçalho: a resposta continua
    /// 201 (o command já foi confirmado), sem <c>Location</c>, e um aviso é registrado em log (sem a URL).</para>
    /// </remarks>
    public static ApiResponseCreatedHttpResult<T> ToCreatedHttpResult<T>(this Result<T> result, Func<T, string> location,
        string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(location);

        if (result.IsFailure)
            return new(ApiResponse<T>.FromResult(result));

        string? relative = TryCreateRelativeLocation(location(result.Value));
        return new(ApiResponse<T>.Created(result.Value, successMessage), relative) { LocationRejected = relative is null };
    }

    /// <summary>
    /// Caminho relativo à aplicação ("/clientes/123"), com os segmentos escapados; <c>null</c> se a URL não for um caminho
    /// relativo seguro.
    /// </summary>
    internal static string? TryCreateRelativeLocation(string? location)
    {
        // Verificado antes de escapar: nada disso pode virar um caminho "válido" por escape
        bool isRelativePath = location is ['/', ..]
            && !location.StartsWith("//", StringComparison.Ordinal)
            && !location.Contains('\\', StringComparison.Ordinal)
            && !location.Any(char.IsControl);
        if (!isRelativePath)
            return null;

        string escaped = EscapeRelativeUrl(location!);
        return escaped.StartsWith("//", StringComparison.Ordinal) || !Uri.IsWellFormedUriString(escaped, UriKind.Relative)
            ? null
            : escaped;
    }

    /// <summary>
    /// Escapa (UTF-8, <c>%XX</c>) os caracteres não permitidos no caminho, na query e no fragmento (RFC 3986), mantendo os
    /// delimitadores e as sequências <c>%XX</c> já escapadas.
    /// </summary>
    private static string EscapeRelativeUrl(string url)
    {
        var builder = new System.Text.StringBuilder(url.Length + 16);
        Span<byte> utf8 = stackalloc byte[4];

        for (int i = 0; i < url.Length; i++)
        {
            char c = url[i];

            if (c == '%' && i + 2 < url.Length && char.IsAsciiHexDigit(url[i + 1]) && char.IsAsciiHexDigit(url[i + 2]))
            {
                builder.Append(c); // já escapado
                continue;
            }

            if (IsAllowed(c))
            {
                builder.Append(c);
                continue;
            }

            // Par substituto (emoji etc.) é escapado junto; substituto isolado vira U+FFFD
            char single = char.IsSurrogate(c) ? (char)0xFFFD : c;
            int length = char.IsHighSurrogate(c) && i + 1 < url.Length && char.IsLowSurrogate(url[i + 1])
                ? System.Text.Encoding.UTF8.GetBytes(url.AsSpan(i++, 2), utf8)
                : System.Text.Encoding.UTF8.GetBytes(new ReadOnlySpan<char>(in single), utf8);

            foreach (byte b in utf8[..length])
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    // unreserved / sub-delims / ":" / "@" / "/" (RFC 3986), mais os delimitadores de query e fragmento
    private static bool IsAllowed(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '-' or '.' or '_' or '~' or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
            or ':' or '@' or '/' or '?' or '#';

    /// <inheritdoc cref="ToHttpResult(Result, string?)"/>
    public static async Task<ApiResponseHttpResult<ApiResponse>> ToHttpResult(this Task<Result> result, string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (await result.ConfigureAwait(false)).ToHttpResult(successMessage);
    }

    /// <inheritdoc cref="ToHttpResult{T}(Result{T}, string?)"/>
    public static async Task<ApiResponseHttpResult<ApiResponse<T>>> ToHttpResult<T>(this Task<Result<T>> result, string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (await result.ConfigureAwait(false)).ToHttpResult(successMessage);
    }

    /// <inheritdoc cref="ToHttpResult{T}(Result{PagedResult{T}}, string?)"/>
    public static async Task<ApiResponseHttpResult<PagedResponse<T>>> ToHttpResult<T>(this Task<Result<PagedResult<T>>> result,
        string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (await result.ConfigureAwait(false)).ToHttpResult(successMessage);
    }

    /// <inheritdoc cref="ToCreatedHttpResult{T}(Result{T}, Func{T, string}, string?)"/>
    public static async Task<ApiResponseCreatedHttpResult<T>> ToCreatedHttpResult<T>(this Task<Result<T>> result,
        Func<T, string> location, string? successMessage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return (await result.ConfigureAwait(false)).ToCreatedHttpResult(location, successMessage);
    }
}
