namespace TEC.Cqrs.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> em memória, para testes, desenvolvimento e aplicações de <b>uma única instância</b>.
/// Com várias instâncias (ou reinícios), use um store compartilhado, como o <c>EfIdempotencyStore&lt;TContext&gt;</c> do TEC.ORM.
/// </summary>
/// <remarks>
/// <para>Thread-safe (as operações sobre uma chave são atômicas). Chave e escopo comparados de forma ordinal.</para>
/// <para>Limites: <see cref="InMemoryIdempotencyStoreOptions.MaxEntriesPerScope"/> entradas por escopo (usuário) e
/// <see cref="InMemoryIdempotencyStoreOptions.MaxEntries"/> no total. No limite do escopo, descarta as expiradas e, se preciso,
/// a resposta concluída do próprio escopo que expira primeiro (um usuário não descarta as respostas dos outros); no limite
/// total, o mesmo entre todas as entradas. Com todas as entradas do limite em andamento, recusa novas reservas
/// (<see cref="InvalidOperationException"/>).</para>
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<IdempotencyKey, Entry> _entries = [];
    private readonly Dictionary<string, HashSet<IdempotencyKey>> _byScope = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly int _maxEntriesPerScope;

    /// <summary>Cria o store.</summary>
    /// <param name="options">Opções (limite de entradas).</param>
    /// <param name="timeProvider">Relógio; <c>null</c> usa o do sistema.</param>
    /// <exception cref="ArgumentOutOfRangeException">Limite de entradas (total ou por escopo) menor que 1.</exception>
    public InMemoryIdempotencyStore(InMemoryIdempotencyStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        _maxEntries = options?.MaxEntries ?? InMemoryIdempotencyStoreOptions.DefaultMaxEntries;
        _maxEntriesPerScope = options?.MaxEntriesPerScope ?? InMemoryIdempotencyStoreOptions.DefaultMaxEntriesPerScope;
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxEntries, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_maxEntriesPerScope, 1, nameof(options));
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

            if (entry is not null)
                Remove(request.Key);
            EnsureCapacity(request.Key.Scope, now);

            var lockId = Guid.NewGuid();
            _entries[request.Key] = new Entry(request.RequestHash, lockId, now + request.LockTimeout, request.Retention);
            if (!_byScope.TryGetValue(request.Key.Scope, out var keys))
                _byScope[request.Key.Scope] = keys = [];
            keys.Add(request.Key);
            return ValueTask.FromResult(IdempotencyBeginResult.Started(lockId));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> CompleteAsync(IdempotencyKey key, Guid lockId, IdempotentResponse response, CancellationToken cancellationToken)
    {
        key.EnsureCreated(nameof(key));
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
        key.EnsureCreated(nameof(key));
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Response is not null || entry.LockId != lockId)
                return ValueTask.FromResult(false);

            Remove(key);
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>Garante espaço para uma entrada nova no escopo e no total (chamado dentro do lock).</summary>
    private void EnsureCapacity(string scope, DateTimeOffset now)
    {
        // Primeiro o escopo: um usuário no limite libera espaço com as próprias entradas, sem tocar nas dos outros
        if (_byScope.TryGetValue(scope, out var scopeKeys) && scopeKeys.Count >= _maxEntriesPerScope)
        {
            MakeRoom(scopeKeys, now, $"O InMemoryIdempotencyStore atingiu o limite de {_maxEntriesPerScope} entradas do escopo, " +
                "todas em andamento. Aumente MaxEntriesPerScope ou use um store compartilhado.");
        }

        if (_entries.Count >= _maxEntries)
        {
            MakeRoom(_entries.Keys, now, $"O InMemoryIdempotencyStore atingiu o limite de {_maxEntries} entradas, todas em andamento. " +
                "Aumente MaxEntries ou use um store compartilhado.");
        }
    }

    /// <summary>
    /// Libera uma vaga entre <paramref name="keys"/>: descarta as expiradas e, se nenhuma expirou, a resposta concluída que
    /// expira primeiro. Uma passagem, sem ordenação (O(n)).
    /// </summary>
    private void MakeRoom(IEnumerable<IdempotencyKey> keys, DateTimeOffset now, string fullMessage)
    {
        List<IdempotencyKey>? expired = null;
        IdempotencyKey? oldest = null;
        DateTimeOffset oldestExpiresAt = DateTimeOffset.MaxValue;
        foreach (var key in keys)
        {
            var entry = _entries[key];
            if (!entry.IsAlive(now))
                (expired ??= []).Add(key);
            else if (entry.Response is not null && entry.ExpiresAt < oldestExpiresAt)
                (oldest, oldestExpiresAt) = (key, entry.ExpiresAt);
        }

        if (expired is not null)
        {
            foreach (var key in expired)
                Remove(key);
            return;
        }

        if (oldest is null)
            throw new InvalidOperationException(fullMessage);

        Remove(oldest.Value);
    }

    /// <summary>Remove a entrada e o índice por escopo (chamado dentro do lock).</summary>
    private void Remove(IdempotencyKey key)
    {
        if (!_entries.Remove(key))
            return;

        if (_byScope.TryGetValue(key.Scope, out var keys) && keys.Remove(key) && keys.Count == 0)
            _byScope.Remove(key.Scope);
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

    /// <summary>Padrão de <see cref="MaxEntriesPerScope"/>.</summary>
    public const int DefaultMaxEntriesPerScope = 1_000;

    /// <summary>Máximo de chaves guardadas. Padrão: 10.000.</summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    /// <summary>
    /// Máximo de chaves guardadas por escopo (usuário): no limite, o escopo descarta as próprias respostas mais antigas, sem
    /// afetar as dos outros. Padrão: 1.000.
    /// </summary>
    public int MaxEntriesPerScope { get; set; } = DefaultMaxEntriesPerScope;
}
