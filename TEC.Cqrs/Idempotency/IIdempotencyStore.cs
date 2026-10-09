namespace TEC.Cqrs.Idempotency;

/// <summary>
/// Guarda as respostas de operações idempotentes (ex.: <c>Idempotency-Key</c> HTTP) com <b>reserva</b>: a primeira
/// requisição com uma chave reserva-a antes de executar; as concorrentes recebem <see cref="IdempotencyBeginStatus.InProgress"/>
/// e não executam a operação de novo.
/// </summary>
/// <remarks>
/// <para>Fluxo: <see cref="TryBeginAsync"/> → executa a operação → <see cref="CompleteAsync"/> (guarda a resposta) ou
/// <see cref="AbandonAsync"/> (libera a chave para nova tentativa, ex.: resposta de erro ou exceção).</para>
/// <para>A reserva expira em <see cref="IdempotencyRequest.LockTimeout"/>: se o processo cair no meio, outra requisição
/// assume a chave depois desse prazo. <see cref="CompleteAsync"/> e <see cref="AbandonAsync"/> só têm efeito com o
/// <see cref="IdempotencyBeginResult.LockId"/> da reserva vigente (quem perdeu a reserva não sobrescreve quem a assumiu).</para>
/// <para>Implementações: <see cref="InMemoryIdempotencyStore"/> (uma instância, testes e desenvolvimento) e a do TEC.ORM
/// (<c>EfIdempotencyStore&lt;TContext&gt;</c>, banco compartilhado entre instâncias). Devem ser thread-safe.</para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Reserva a chave, ou informa a resposta já guardada, a execução em andamento ou o conflito de conteúdo.</summary>
    /// <param name="request">Chave, hash da requisição e prazos.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>O estado da chave; em <see cref="IdempotencyBeginStatus.Started"/>, o <see cref="IdempotencyBeginResult.LockId"/> da reserva.</returns>
    ValueTask<IdempotencyBeginResult> TryBeginAsync(IdempotencyRequest request, CancellationToken cancellationToken);

    /// <summary>Guarda a resposta da operação concluída, encerrando a reserva.</summary>
    /// <param name="key">Chave reservada.</param>
    /// <param name="lockId">Identificador da reserva (de <see cref="TryBeginAsync"/>).</param>
    /// <param name="response">Resposta a devolver nas repetições.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns><c>false</c> se a reserva não é mais desta execução (expirou e foi assumida por outra).</returns>
    ValueTask<bool> CompleteAsync(IdempotencyKey key, Guid lockId, IdempotentResponse response, CancellationToken cancellationToken);

    /// <summary>Libera a chave sem guardar resposta (a próxima requisição com a chave executa de novo).</summary>
    /// <param name="key">Chave reservada.</param>
    /// <param name="lockId">Identificador da reserva (de <see cref="TryBeginAsync"/>).</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns><c>false</c> se a reserva não é mais desta execução.</returns>
    ValueTask<bool> AbandonAsync(IdempotencyKey key, Guid lockId, CancellationToken cancellationToken);
}

/// <summary>Chave idempotente: o escopo (normalmente o usuário) mais o valor informado pelo cliente.</summary>
/// <remarks>
/// O escopo evita que um usuário reutilize (e leia) a resposta guardada para outro com a mesma chave.
/// Limites: escopo até <see cref="MaxScopeLength"/> e chave até <see cref="MaxKeyLength"/> caracteres, sem caracteres de controle.
/// </remarks>
public readonly record struct IdempotencyKey
{
    /// <summary>Tamanho máximo do escopo.</summary>
    public const int MaxScopeLength = 200;

    /// <summary>Tamanho máximo da chave.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>Cria a chave.</summary>
    /// <param name="scope">Escopo (ex.: identificador do usuário).</param>
    /// <param name="key">Valor informado pelo cliente.</param>
    /// <exception cref="ArgumentException">Escopo ou chave vazios, longos demais ou com caracteres de controle.</exception>
    public IdempotencyKey(string scope, string key)
    {
        Validate(scope, MaxScopeLength, nameof(scope));
        Validate(key, MaxKeyLength, nameof(key));
        Scope = scope;
        Key = key;
    }

    /// <summary>Escopo da chave.</summary>
    public string Scope { get; }

    /// <summary>Valor informado pelo cliente.</summary>
    public string Key { get; }

    /// <summary>Indica se o texto é aceito como chave (não vazio, até <paramref name="maxLength"/>, sem controle).</summary>
    /// <param name="value">Texto.</param>
    /// <param name="maxLength">Tamanho máximo.</param>
    /// <returns><c>true</c> se for válido.</returns>
    public static bool IsValid(string? value, int maxLength = MaxKeyLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength && !value.Any(char.IsControl);

    private static void Validate(string value, int maxLength, string paramName)
    {
        if (!IsValid(value, maxLength))
            throw new ArgumentException($"Valor inválido: vazio, com mais de {maxLength} caracteres ou com caracteres de controle.", paramName);
    }
}

/// <summary>Pedido de reserva de uma chave idempotente.</summary>
/// <param name="Key">Chave.</param>
/// <param name="RequestHash">Hash do conteúdo da requisição (mesma chave com outro conteúdo é conflito).</param>
/// <param name="Retention">Por quanto tempo a resposta concluída é guardada.</param>
/// <param name="LockTimeout">Prazo da reserva: depois dele, outra requisição pode assumir a chave.</param>
public sealed record IdempotencyRequest(IdempotencyKey Key, string RequestHash, TimeSpan Retention, TimeSpan LockTimeout)
{
    /// <summary>Tamanho máximo do hash.</summary>
    public const int MaxRequestHashLength = 128;

    /// <summary>Valida os campos.</summary>
    /// <exception cref="ArgumentException">Hash vazio ou longo demais, ou prazos não positivos.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RequestHash) || RequestHash.Length > MaxRequestHashLength)
            throw new ArgumentException($"O hash da requisição deve ter de 1 a {MaxRequestHashLength} caracteres.", nameof(RequestHash));
        if (Retention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Retention), "A retenção deve ser maior que zero.");
        if (LockTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(LockTimeout), "O prazo da reserva deve ser maior que zero.");
    }
}

/// <summary>Resposta guardada de uma operação idempotente.</summary>
/// <param name="StatusCode">Status (ex.: HTTP 201).</param>
/// <param name="ContentType">Tipo de conteúdo, se houver.</param>
/// <param name="Headers">Cabeçalhos a repetir (ex.: <c>Location</c>, <c>ETag</c>).</param>
/// <param name="Body">Corpo da resposta.</param>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body);

/// <summary>Estado de uma chave idempotente em <see cref="IIdempotencyStore.TryBeginAsync"/>.</summary>
public enum IdempotencyBeginStatus
{
    /// <summary>Chave reservada para esta execução: execute a operação.</summary>
    Started = 0,

    /// <summary>A operação já foi concluída: devolva a resposta guardada.</summary>
    Completed = 1,

    /// <summary>Outra execução com a mesma chave está em andamento.</summary>
    InProgress = 2,

    /// <summary>A chave já foi usada com outro conteúdo.</summary>
    Mismatch = 3
}

/// <summary>Resultado de <see cref="IIdempotencyStore.TryBeginAsync"/>.</summary>
public sealed class IdempotencyBeginResult
{
    private static readonly IdempotencyBeginResult InProgressInstance = new(IdempotencyBeginStatus.InProgress, Guid.Empty, null);
    private static readonly IdempotencyBeginResult MismatchInstance = new(IdempotencyBeginStatus.Mismatch, Guid.Empty, null);

    private IdempotencyBeginResult(IdempotencyBeginStatus status, Guid lockId, IdempotentResponse? response)
    {
        Status = status;
        LockId = lockId;
        Response = response;
    }

    /// <summary>Estado da chave.</summary>
    public IdempotencyBeginStatus Status { get; }

    /// <summary>Identificador da reserva (só em <see cref="IdempotencyBeginStatus.Started"/>).</summary>
    public Guid LockId { get; }

    /// <summary>Resposta guardada (só em <see cref="IdempotencyBeginStatus.Completed"/>).</summary>
    public IdempotentResponse? Response { get; }

    /// <summary>Chave reservada para esta execução.</summary>
    /// <param name="lockId">Identificador da reserva.</param>
    /// <returns>O resultado.</returns>
    public static IdempotencyBeginResult Started(Guid lockId) => new(IdempotencyBeginStatus.Started, lockId, null);

    /// <summary>Operação já concluída.</summary>
    /// <param name="response">Resposta guardada.</param>
    /// <returns>O resultado.</returns>
    public static IdempotencyBeginResult Completed(IdempotentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(IdempotencyBeginStatus.Completed, Guid.Empty, response);
    }

    /// <summary>Outra execução em andamento.</summary>
    public static IdempotencyBeginResult InProgress => InProgressInstance;

    /// <summary>Chave usada com outro conteúdo.</summary>
    public static IdempotencyBeginResult Mismatch => MismatchInstance;
}
