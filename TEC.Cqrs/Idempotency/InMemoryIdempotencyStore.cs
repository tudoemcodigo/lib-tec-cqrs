namespace TEC.Cqrs.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> em memória, para testes, desenvolvimento e aplicações de <b>uma única instância</b>.
/// Com várias instâncias (ou reinícios), use um store compartilhado, como o <c>EfIdempotencyStore&lt;TContext&gt;</c> do TEC.ORM.
/// </summary>
/// <remarks>
/// Thread-safe (as operações sobre uma chave são atômicas). Limitado a <see cref="InMemoryIdempotencyStoreOptions.MaxEntries"/>
/// entradas: cheio, descarta as expiradas e, se preciso, as respostas concluídas que expiram primeiro; com todas as entradas
/// em andamento, recusa novas reservas (<see cref="InvalidOperationException"/>).
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<IdempotencyKey, Entry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;

    /// <summary>Cria o store.</summary>
    /// <param name="options">Opções (limite de entradas).</param>
    /// <param name="timeProvider">Relógio; <c>null</c> usa o do sistema.</param>
    /// <exception cref="ArgumentOutOfRangeException">Limite de entradas menor que 1.</exception>
    public InMemoryIdempotencyStore(InMemoryIdempotencyStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        _maxEntries = options?.MaxEntries ?? InMemoryIdempotencyStoreOptions.DefaultMaxEntries;
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxEntries, 1, nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Quantidade de entradas guardadas (inclusive expiradas ainda não descartadas).</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    /// <inheritdoc />
    public ValueTask<IdempotencyBeginResult> TryBeginAsync(IdempotencyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (_entries.TryGetValue(request.Key, out var entry) && entry.IsAlive(now))
            {
                if (!string.Equals(entry.RequestHash, request.RequestHash, StringComparison.Ordinal))
                    return ValueTask.FromResult(IdempotencyBeginResult.Mismatch);

                return ValueTask.FromResult(entry.Response is { } response
                    ? IdempotencyBeginResult.Completed(response)
                    : IdempotencyBeginResult.InProgress);
            }

            if (entry is null)
                EnsureCapacity(now);

            var lockId = Guid.NewGuid();
            _entries[request.Key] = new Entry(request.RequestHash, lockId, now + request.LockTimeout, request.Retention);
            return ValueTask.FromResult(IdempotencyBeginResult.Started(lockId));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CompleteAsync(IdempotencyKey key, Guid lockId, IdempotentResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var now = _timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Response is not null || entry.LockId != lockId)
                return ValueTask.FromResult(false);

            entry.Response = Copy(response);
            entry.ExpiresAt = now + entry.Retention;
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> AbandonAsync(IdempotencyKey key, Guid lockId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Response is not null || entry.LockId != lockId)
                return ValueTask.FromResult(false);

            _entries.Remove(key);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Garante espaço para uma entrada nova (chamado dentro do lock).</summary>
    private void EnsureCapacity(DateTimeOffset now)
    {
        if (_entries.Count < _maxEntries)
            return;

        foreach (var expired in _entries.Where(e => !e.Value.IsAlive(now)).Select(e => e.Key).ToList())
            _entries.Remove(expired);

        if (_entries.Count < _maxEntries)
            return;

        var oldest = _entries.Where(e => e.Value.Response is not null).OrderBy(e => e.Value.ExpiresAt).Select(e => (KeyValuePair<IdempotencyKey, Entry>?)e).FirstOrDefault();
        if (oldest is null)
        {
            throw new InvalidOperationException(
                $"O InMemoryIdempotencyStore atingiu o limite de {_maxEntries} entradas, todas em andamento. Aumente MaxEntries ou use um store compartilhado.");
        }

        _entries.Remove(oldest.Value.Key);
    }

    // Cópia defensiva: alterações posteriores no buffer do chamador não afetam a resposta guardada
    private static IdempotentResponse Copy(IdempotentResponse response) => response with
    {
        Headers = new Dictionary<string, string>(response.Headers, StringComparer.OrdinalIgnoreCase),
        Body = response.Body.ToArray()
    };

    private sealed class Entry(string requestHash, Guid lockId, DateTimeOffset lockedUntil, TimeSpan retention)
    {
        public string RequestHash { get; } = requestHash;

        public Guid LockId { get; } = lockId;

        public DateTimeOffset LockedUntil { get; } = lockedUntil;

        public TimeSpan Retention { get; } = retention;

        public IdempotentResponse? Response { get; set; }

        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.MaxValue;

        /// <summary>Concluída e dentro da retenção, ou reservada e dentro do prazo.</summary>
        public bool IsAlive(DateTimeOffset now) => Response is not null ? ExpiresAt > now : LockedUntil > now;
    }
}

/// <summary>Opções do <see cref="InMemoryIdempotencyStore"/>.</summary>
public sealed class InMemoryIdempotencyStoreOptions
{
    /// <summary>Padrão de <see cref="MaxEntries"/>.</summary>
    public const int DefaultMaxEntries = 10_000;

    /// <summary>Máximo de chaves guardadas. Padrão: 10.000.</summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;
}
