using System.Collections.Concurrent;
using TEC.Cqrs.Persistence;

namespace TEC.Cqrs.SampleApi;

/// <summary>Pedido de um cliente.</summary>
public sealed record Order(Guid Id, string CustomerId, string Description, decimal Amount, bool Refunded = false);

/// <summary>Opções da API de exemplo (seção <c>Exemplo</c> da configuração).</summary>
public sealed record SampleOptions
{
    [ConfigurationKeyName("Clientes")]
    public int Customers { get; init; } = SampleData.DefaultCustomers;
    [ConfigurationKeyName("PedidosPorCliente")]
    public int OrdersPerCustomer { get; init; } = SampleData.DefaultOrdersPerCustomer;
    [ConfigurationKeyName("MaxPedidosPorCliente")]
    public int MaxOrdersPerCustomer { get; init; } = SampleData.DefaultMaxOrdersPerCustomer;
}

/// <summary>
/// "Banco" em memória (Singleton). Só recebe alterações confirmadas pelo <see cref="InMemoryUnitOfWork"/>. Cada cliente
/// mantém, além dos pedidos iniciais (que nunca saem), no máximo <see cref="SampleOptions.MaxOrdersPerCustomer"/> pedidos
/// criados (os mais antigos saem primeiro), para a memória ficar estável em cargas longas.
/// </summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<Guid, Order> _orders = new();
    private readonly ConcurrentDictionary<string, CustomerIndex> _byCustomer = new(StringComparer.Ordinal);
    private readonly int _maxCreatedPerCustomer;
    private long _created;

    public OrderStore(SampleOptions options)
    {
        _maxCreatedPerCustomer = Math.Max(options.MaxOrdersPerCustomer, 1);
        for (int c = 0; c < options.Customers; c++)
        {
            var index = _byCustomer.GetOrAdd(SampleData.CustomerId(c), _ => new CustomerIndex());
            for (int p = 0; p < options.OrdersPerCustomer; p++)
            {
                var order = new Order(SampleData.OrderId(c, p), SampleData.CustomerId(c), $"Pedido inicial {p}", 10m + p);
                _orders[order.Id] = order;
                index.Initial.Add(order.Id);
            }
        }
    }

    /// <summary>Pedidos criados e confirmados desde o início (sem os iniciais).</summary>
    public long Created => Interlocked.Read(ref _created);

    public Order? Get(Guid id) => _orders.GetValueOrDefault(id);

    /// <summary>Página dos pedidos do cliente: os criados (do mais recente para o mais antigo) e depois os iniciais.</summary>
    public (IReadOnlyList<Order> Items, long Total) ListPage(string customerId, int page, int pageSize)
    {
        if (!_byCustomer.TryGetValue(customerId, out var index))
            return ([], 0);

        Guid[] selected;
        int total;
        lock (index)
        {
            total = index.Created.Count + index.Initial.Count;
            selected = [.. index.Created.Reverse().Concat(index.Initial).Skip((page - 1) * pageSize).Take(pageSize)];
        }

        return ([.. selected.Select(Get).OfType<Order>()], total);
    }

    internal void Add(Order order)
    {
        _orders[order.Id] = order;
        var index = _byCustomer.GetOrAdd(order.CustomerId, _ => new CustomerIndex());
        lock (index)
        {
            index.Created.Enqueue(order.Id);
            if (index.Created.Count > _maxCreatedPerCustomer)
                _orders.TryRemove(index.Created.Dequeue(), out _);
        }

        Interlocked.Increment(ref _created);
    }

    internal void Refund(Guid id)
    {
        // TryUpdate: um pedido removido pelo limite por cliente não volta ao dicionário
        while (_orders.TryGetValue(id, out var current) && !_orders.TryUpdate(id, current with { Refunded = true }, current))
        {
        }
    }

    // Pedidos de um cliente; acessado sob lock da própria instância
    private sealed class CustomerIndex
    {
        public List<Guid> Initial { get; } = [];
        public Queue<Guid> Created { get; } = new();
    }
}

/// <summary>
/// Unidade de trabalho em memória (Scoped): as alterações ficam pendentes até o commit e são descartadas no rollback,
/// como numa transação de banco.
/// </summary>
public sealed class InMemoryUnitOfWork(OrderStore store) : IUnitOfWork
{
    private List<Action<OrderStore>>? _pending;

    public bool HasActiveTransaction => _pending is not null;

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        _pending = [];
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        var pending = _pending ?? throw new InvalidOperationException("Nenhuma transação aberta.");
        _pending = null;
        foreach (var change in pending)
            change(store);
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        _pending = null;
        return Task.CompletedTask;
    }

    /// <summary>Registra uma alteração: pendente na transação aberta ou aplicada na hora, sem transação.</summary>
    public void Register(Action<OrderStore> change)
    {
        if (_pending is null)
            change(store);
        else
            _pending.Add(change);
    }
}

/// <summary>
/// Contadores da API de exemplo (Singleton), usados pelos testes para verificar o que aconteceu do lado do servidor:
/// handlers executados e notificações publicadas após o commit.
/// </summary>
public sealed class SampleMetrics
{
    private readonly ConcurrentDictionary<string, long> _handlers = new(StringComparer.Ordinal);
    private long _orderCreatedEvents;

    /// <summary>Notificações <see cref="OrderCreatedEvent"/> recebidas (publicadas após o commit).</summary>
    public long OrderCreatedEvents => Interlocked.Read(ref _orderCreatedEvents);

    /// <summary>Execuções do handler <paramref name="handler"/>.</summary>
    public long Executions(string handler) => _handlers.GetValueOrDefault(handler);

    internal void HandlerExecuted(string handler) => _handlers.AddOrUpdate(handler, 1, (_, n) => n + 1);

    internal void OrderCreated() => Interlocked.Increment(ref _orderCreatedEvents);
}
