namespace TEC.Cqrs.Idempotency;

/// <summary>
/// <see cref="IIdempotencyStore"/> em memória, para testes, desenvolvimento e aplicações de <b>uma única instância</b>.
/// Com várias instâncias (ou reinícios), use um store compartilhado, como o <c>EfIdempotencyStore&lt;TContext&gt;</c> do TEC.ORM.
/// </summary>
/// <remarks>
/// <para>Thread-safe (as operações sobre uma chave são atômicas). Chave e escopo comparados de forma ordinal.</para>
/// <para>Limites: <see cref="InMemoryIdempotencyStoreOptions.MaxEntriesPerScope"/> entradas por escopo (usuário),
/// <see cref="InMemoryIdempotencyStoreOptions.MaxEntries"/> entradas no total e
/// <see cref="InMemoryIdempotencyStoreOptions.MaxTotalBytes"/> bytes de respostas guardadas.</para>
/// <para>Despejo: para abrir espaço, o store descarta primeiro as entradas expiradas (de qualquer escopo) e, se não bastar,
/// as respostas concluídas do <b>próprio escopo</b> de quem está pedindo, das que expiram primeiro. Nunca descarta uma
/// resposta ainda válida de outro escopo: isso faria a repetição de outro usuário executar a operação de novo, sem aviso.
/// Sem espaço depois disso, falha fechado com <see cref="InvalidOperationException"/>: em <see cref="TryBeginAsync"/>,
/// antes de executar a operação; em <see cref="CompleteAsync"/> (orçamento de bytes), a reserva continua em andamento até
/// o <see cref="IdempotencyRequest.LockTimeout"/>.</para>
/// </remarks>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<IdempotencyKey, Entry> _entries = [];
    private readonly Dictionary<string, HashSet<IdempotencyKey>> _byScope = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly int _maxEntriesPerScope;
    private readonly long _maxTotalBytes;
    private long _totalBytes;

    /// <summary>Cria o store.</summary>
    /// <param name="options">Opções (limite de entradas).</param>
    /// <param name="timeProvider">Relógio; <c>null</c> usa o do sistema.</param>
    /// <exception cref="ArgumentOutOfRangeException">Limite de entradas (total ou por escopo) ou de bytes menor que 1.</exception>
    public InMemoryIdempotencyStore(InMemoryIdempotencyStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        options ??= new InMemoryIdempotencyStoreOptions();
        options.Validate(nameof(options));
        _maxEntries = options.MaxEntries;
        _maxEntriesPerScope = options.MaxEntriesPerScope;
        _maxTotalBytes = options.MaxTotalBytes;
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

    /// <summary>Bytes estimados das respostas guardadas (inclusive expiradas ainda não descartadas).</summary>
    internal long TotalBytes
    {
        get
        {
            lock (_sync)
                return _totalBytes;
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Sem espaço (entradas ou bytes) sem descartar respostas válidas de outros escopos.</exception>
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
    /// <exception cref="InvalidOperationException">
    /// A resposta não cabe em <see cref="InMemoryIdempotencyStoreOptions.MaxTotalBytes"/> sem descartar respostas válidas de
    /// outros escopos (a reserva continua em andamento até o <see cref="IdempotencyRequest.LockTimeout"/>).
    /// </exception>
    public ValueTask<bool> CompleteAsync(IdempotencyKey key, Guid lockId, IdempotentResponse response, CancellationToken cancellationToken)
    {
        key.EnsureCreated(nameof(key));
        ArgumentNullException.ThrowIfNull(response);
        var now = _timeProvider.GetUtcNow();
        long size = EstimateSize(response);

        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Response is not null || entry.LockId != lockId)
                return ValueTask.FromResult(false);

            if (size > _maxTotalBytes || !TryFreeBytes(key.Scope, now, size))
            {
                throw new InvalidOperationException(
                    $"O InMemoryIdempotencyStore não tem espaço para guardar a resposta ({size} bytes) dentro de MaxTotalBytes " +
                    $"({_maxTotalBytes} bytes) sem descartar respostas válidas de outros escopos. Aumente MaxTotalBytes, " +
                    "reduza a retenção ou use um store compartilhado.");
            }

            entry.Response = Copy(response);
            entry.Size = size;
            entry.ExpiresAt = now + entry.Retention;
            _totalBytes += size;
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

    /// <summary>Garante espaço para uma entrada nova no escopo, no total e no orçamento de bytes (chamado dentro do lock).</summary>
    private void EnsureCapacity(string scope, DateTimeOffset now)
    {
        // Primeiro o escopo: um usuário no limite libera espaço com as próprias entradas, sem tocar nas dos outros
        if (_byScope.TryGetValue(scope, out var scopeKeys) && scopeKeys.Count >= _maxEntriesPerScope
            && !RemoveExpired(scopeKeys, now) && !EvictOldestCompleted(scope, now, long.MaxValue))
        {
            throw new InvalidOperationException(
                $"O InMemoryIdempotencyStore atingiu o limite de {_maxEntriesPerScope} entradas do escopo, todas em andamento. " +
                "Aumente MaxEntriesPerScope ou use um store compartilhado.");
        }

        // No total: expiradas de qualquer escopo, depois só respostas do próprio escopo; nunca a resposta válida de outro
        if (_entries.Count >= _maxEntries && !RemoveExpired(_entries.Keys, now) && !EvictOldestCompleted(scope, now, long.MaxValue))
        {
            throw new InvalidOperationException(
                $"O InMemoryIdempotencyStore atingiu o limite de {_maxEntries} entradas, ocupadas por reservas em andamento e " +
                "respostas ainda válidas de outros escopos (que não são descartadas). Aumente MaxEntries ou use um store compartilhado.");
        }

        // Orçamento esgotado: recusa antes de executar a operação, em vez de falhar só na hora de guardar a resposta
        if (!TryFreeBytes(scope, now, 1))
        {
            throw new InvalidOperationException(
                $"O InMemoryIdempotencyStore atingiu MaxTotalBytes ({_maxTotalBytes} bytes) com respostas ainda válidas de " +
                "outros escopos (que não são descartadas). Aumente MaxTotalBytes, reduza a retenção ou use um store compartilhado.");
        }
    }

    /// <summary>
    /// Libera bytes até caber <paramref name="needed"/>: descarta as expiradas (de qualquer escopo) e, se não bastar, as
    /// respostas concluídas de <paramref name="scope"/> que expiram primeiro. <c>false</c> se nem assim couber.
    /// </summary>
    private bool TryFreeBytes(string scope, DateTimeOffset now, long needed)
    {
        if (_totalBytes + needed <= _maxTotalBytes)
            return true;

        RemoveExpired(_entries.Keys, now);
        if (_totalBytes + needed <= _maxTotalBytes)
            return true;

        EvictOldestCompleted(scope, now, _totalBytes + needed - _maxTotalBytes);
        return _totalBytes + needed <= _maxTotalBytes;
    }

    /// <summary>Remove as entradas expiradas entre <paramref name="keys"/>; <c>true</c> se removeu alguma.</summary>
    private bool RemoveExpired(IEnumerable<IdempotencyKey> keys, DateTimeOffset now)
    {
        List<IdempotencyKey>? expired = null;
        foreach (var key in keys)
        {
            if (!_entries[key].IsAlive(now))
                (expired ??= []).Add(key);
        }

        if (expired is null)
            return false;

        foreach (var key in expired)
            Remove(key);
        return true;
    }

    /// <summary>
    /// Descarta respostas concluídas e válidas de <paramref name="scope"/>, das que expiram primeiro, até liberar
    /// <paramref name="bytesToFree"/> bytes (<see cref="long.MaxValue"/>: só uma). Reservas em andamento nunca saem.
    /// <c>true</c> se descartou alguma.
    /// </summary>
    private bool EvictOldestCompleted(string scope, DateTimeOffset now, long bytesToFree)
    {
        if (!_byScope.TryGetValue(scope, out var keys))
            return false;

        var completed = keys
            .Select(key => (Key: key, Entry: _entries[key]))
            .Where(item => item.Entry.Response is not null && item.Entry.IsAlive(now))
            .OrderBy(item => item.Entry.ExpiresAt)
            .ToList();

        bool evicted = false;
        long freed = 0;
        foreach (var (key, entry) in completed)
        {
            freed += entry.Size;
            Remove(key);
            evicted = true;
            if (bytesToFree == long.MaxValue || freed >= bytesToFree)
                break;
        }

        return evicted;
    }

    /// <summary>Remove a entrada, o índice por escopo e os bytes contabilizados (chamado dentro do lock).</summary>
    private void Remove(IdempotencyKey key)
    {
        if (!_entries.Remove(key, out var entry))
            return;

        _totalBytes -= entry.Size;
        if (_byScope.TryGetValue(key.Scope, out var keys) && keys.Remove(key) && keys.Count == 0)
            _byScope.Remove(key.Scope);
    }

    /// <summary>
    /// Tamanho estimado da resposta guardada: o corpo mais o texto do content type e dos cabeçalhos (2 bytes por caractere,
    /// como na memória do .NET).
    /// </summary>
    internal static long EstimateSize(IdempotentResponse response)
    {
        long size = response.Body.Length + (2L * (response.ContentType?.Length ?? 0));
        foreach (var (name, value) in response.Headers)
            size += 2L * ((name?.Length ?? 0) + (value?.Length ?? 0));
        return size;
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

        /// <summary>Bytes contabilizados em <c>_totalBytes</c> (0 enquanto a reserva estiver em andamento).</summary>
        public long Size { get; set; }

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

    /// <summary>Padrão de <see cref="MaxTotalBytes"/>: 256 MiB.</summary>
    public const long DefaultMaxTotalBytes = 256L * 1024 * 1024;

    /// <summary>Máximo de chaves guardadas. Padrão: 10.000.</summary>
    public int MaxEntries { get; set; } = DefaultMaxEntries;

    /// <summary>
    /// Máximo de chaves guardadas por escopo (usuário): no limite, o escopo descarta as próprias respostas mais antigas, sem
    /// afetar as dos outros. Padrão: 1.000.
    /// </summary>
    public int MaxEntriesPerScope { get; set; } = DefaultMaxEntriesPerScope;

    /// <summary>
    /// Orçamento global, em bytes, das respostas guardadas (corpo mais o texto do content type e dos cabeçalhos, estimado
    /// a 2 bytes por caractere). Sem ele, o pior caso seria <see cref="MaxEntries"/> vezes o tamanho máximo da resposta
    /// (10.000 × 1 MiB no padrão do middleware HTTP, cerca de 10 GiB). No limite, descarta as expiradas e as respostas do
    /// próprio escopo; não cabendo, recusa (<see cref="InvalidOperationException"/>). Padrão: 256 MiB.
    /// </summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;

    /// <summary>Valida os limites (chamado no registro e no construtor do store).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Algum limite menor que 1.</exception>
    internal void Validate(string paramName)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntries, 1, paramName);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntriesPerScope, 1, paramName);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalBytes, 1L, paramName);
    }
}
