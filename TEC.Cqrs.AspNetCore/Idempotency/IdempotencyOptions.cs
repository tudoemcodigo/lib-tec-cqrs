using System.Security.Claims;
using TEC.Cqrs.Idempotency;

namespace TEC.Cqrs.AspNetCore.Idempotency;

/// <summary>Configuração da idempotência HTTP (<c>AddIdempotency</c> e <c>UseTecIdempotency</c>).</summary>
/// <remarks>Validada na inicialização: valores fora dos limites fazem a aplicação falhar ao subir.</remarks>
public sealed class IdempotencyOptions
{
    /// <summary>Cabeçalho com a chave enviada pelo cliente. Padrão: <c>Idempotency-Key</c>.</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>Cabeçalho incluído nas respostas repetidas (valor <c>true</c>). Padrão: <c>Idempotent-Replayed</c>.</summary>
    public string ReplayedHeaderName { get; set; } = "Idempotent-Replayed";

    /// <summary>Tamanho máximo da chave (1 a <see cref="IdempotencyKey.MaxKeyLength"/>). Padrão: 100.</summary>
    public int MaxKeyLength { get; set; } = 100;

    /// <summary>
    /// Tamanho máximo, em bytes, do corpo da requisição lido para o hash. Maior que isso: HTTP 413 sem executar a operação.
    /// Padrão: 1 MiB.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Tamanho máximo, em bytes, da resposta guardada. Uma resposta maior é enviada normalmente, mas não é guardada (a chave é
    /// liberada e um aviso é registrado). Padrão: 1 MiB.
    /// </summary>
    public int MaxResponseBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Por quanto tempo a resposta é guardada. Padrão: 24 horas.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Prazo da reserva: se a execução não terminar nele (ex.: processo derrubado), outra requisição com a mesma chave pode
    /// executar. Deve ser maior que o tempo máximo de uma requisição. Padrão: 2 minutos.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Cabeçalhos da resposta guardados e repetidos. Padrão: <c>Location</c>, <c>ETag</c>, <c>Content-Location</c>, <c>Last-Modified</c>.</summary>
    public ISet<string> StoredResponseHeaders { get; } =
        new HashSet<string>(["Location", "ETag", "Content-Location", "Last-Modified"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claims que identificam o usuário (escopo da chave), na ordem de preferência. Padrão: <c>tec_uid</c> (TEC.Security),
    /// <c>oid</c> do Entra ID, <see cref="ClaimTypes.NameIdentifier"/> e <c>sub</c>. Requisição sem nenhum deles (ex.:
    /// anônima) é executada sem idempotência.
    /// </summary>
    public IList<string> UserIdClaimTypes { get; } =
    [
        "tec_uid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
        "oid",
        ClaimTypes.NameIdentifier,
        "sub"
    ];

    /// <summary>Erros de configuração (vazio quando válida).</summary>
    internal IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(HeaderName))
            yield return "HeaderName não pode ser vazio.";
        if (string.IsNullOrWhiteSpace(ReplayedHeaderName))
            yield return "ReplayedHeaderName não pode ser vazio.";
        if (MaxKeyLength is < 1 or > IdempotencyKey.MaxKeyLength)
            yield return $"MaxKeyLength deve estar entre 1 e {IdempotencyKey.MaxKeyLength}.";
        if (MaxRequestBodyBytes < 0)
            yield return "MaxRequestBodyBytes não pode ser negativo.";
        if (MaxResponseBodyBytes < 0)
            yield return "MaxResponseBodyBytes não pode ser negativo.";
        if (Retention <= TimeSpan.Zero)
            yield return "Retention deve ser maior que zero.";
        if (LockTimeout <= TimeSpan.Zero)
            yield return "LockTimeout deve ser maior que zero.";
        if (UserIdClaimTypes.Count == 0 || UserIdClaimTypes.Any(string.IsNullOrWhiteSpace))
            yield return "UserIdClaimTypes deve ter ao menos um tipo de claim, nenhum em branco.";
    }
}
