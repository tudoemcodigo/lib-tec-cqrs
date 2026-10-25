using Microsoft.AspNetCore.Http;

namespace TEC.Cqrs.AspNetCore;

/// <summary>
/// Extensões e utilitários HTTP para concorrência otimista (cabeçalhos <c>ETag</c> e <c>If-Match</c>, RFC 9110).
/// </summary>
public static class HttpConcurrencyExtensions
{
    private static readonly byte[] TokenInvalido = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    /// <summary>Gera uma representação ETag HTTP com aspas a partir de um array de bytes (<paramref name="rowVersion"/>).</summary>
    public static string GerarETag(byte[]? rowVersion)
    {
        if (rowVersion is null || rowVersion.Length == 0)
            return "\"\"";

        return $"\"{Convert.ToBase64String(rowVersion)}\"";
    }

    /// <summary>Define o cabeçalho <c>ETag</c> na resposta HTTP a partir do <paramref name="rowVersion"/>.</summary>
    public static void SetETag(this HttpResponse response, byte[]? rowVersion)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (rowVersion is { Length: > 0 })
            response.Headers.ETag = GerarETag(rowVersion);
    }

    /// <summary>
    /// Extrai os bytes de um cabeçalho <c>If-Match</c> (suporta prefixo fraco <c>W/</c>, aspas e codificação Base64).
    /// Devolve nulo se omitido, vazio ou wildcard (<c>"*"</c>). Se o valor for inválido, devolve um token sentinela que
    /// falha em comparações seguras.
    /// </summary>
    public static byte[]? ExtrairRowVersion(string? ifMatchHeader)
    {
        if (string.IsNullOrWhiteSpace(ifMatchHeader))
            return null;

        var header = ifMatchHeader.Trim();
        if (header == "*")
            return null;

        if (header.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            header = header[2..].Trim();

        header = header.Trim('"');

        Span<byte> buffer = stackalloc byte[32];
        if (Convert.TryFromBase64String(header, buffer, out int bytesEscritos))
            return buffer[..bytesEscritos].ToArray();

        return TokenInvalido;
    }

    /// <summary>Obtém e extrai o <c>RowVersion</c> a partir do cabeçalho <c>If-Match</c> da requisição HTTP.</summary>
    public static byte[]? GetIfMatchRowVersion(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExtrairRowVersion(request.Headers.IfMatch.ToString());
    }
}
