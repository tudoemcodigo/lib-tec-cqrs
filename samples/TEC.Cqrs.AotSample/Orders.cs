using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using TEC.Core.Common.Results;
using TEC.Core.Responses;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.AotSample;

/// <summary>Requisição de um cliente: o authorizer registrado para a interface confere se o usuário é o dono.</summary>
public interface ICustomerRequest : IBaseRequest
{
    /// <summary>Cliente dono do pedido.</summary>
    string CustomerId { get; }
}

/// <summary>Cria um pedido (autorização só pelo authorizer da interface, sem atributo).</summary>
public sealed record CreateOrderCommand(string CustomerId, int Quantity) : ICommand<Guid>, ICustomerRequest;

/// <summary>Obtém um pedido do cliente.</summary>
public sealed record GetOrderQuery(string CustomerId, Guid OrderId) : IQuery<OrderDto>, ICustomerRequest;

/// <summary>Pedido devolvido pela consulta.</summary>
public sealed record OrderDto(Guid Id, string CustomerId, int Quantity);

/// <summary>Pedido criado (publicado só depois do commit).</summary>
public sealed record OrderCreatedEvent(Guid OrderId) : INotification;

/// <summary>Pedidos em memória e o registro do que aconteceu, para as verificações do <c>Program</c>.</summary>
public sealed class OrderStore
{
    private readonly ConcurrentDictionary<Guid, OrderDto> _orders = new();
    private readonly ConcurrentQueue<string> _events = new();

    public IReadOnlyCollection<string> Events => _events;

    public void Add(OrderDto order) => _orders[order.Id] = order;

    public OrderDto? Find(Guid id) => _orders.GetValueOrDefault(id);

    public void Record(string entry) => _events.Enqueue(entry);
}

/// <summary>Só o dono do pedido (claim <c>sub</c>) é autorizado.</summary>
public sealed class CustomerOwnerAuthorizer(IPrincipalAccessor principal) : IRequestAuthorizer<ICustomerRequest>
{
    public Task<Result> AuthorizeAsync(ICustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool owner = string.Equals(principal.Principal?.FindFirst("sub")?.Value, request.CustomerId, StringComparison.Ordinal);
        return Task.FromResult(owner
            ? Result.Success()
            : Result.Failure(Error.Forbidden("PEDIDO_DE_OUTRO_CLIENTE", "O pedido pertence a outro cliente.")));
    }
}

/// <summary>Validador próprio (sem FluentValidation).</summary>
public sealed class CreateOrderValidator : IRequestValidator<CreateOrderCommand>
{
    public Task<Result> ValidateAsync(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(request.Quantity is >= 1 and <= 100
            ? Result.Success()
            : Result.Failure(Error.Validation("QUANTIDADE_INVALIDA", "A quantidade deve estar entre 1 e 100.", "quantity")));
    }
}

public sealed class CreateOrderHandler(OrderStore store, IPublisher publisher) : ICommandHandler<CreateOrderCommand, Guid>
{
    public Task<Result<Guid>> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = new OrderDto(Guid.NewGuid(), request.CustomerId, request.Quantity);
        store.Add(order);
        store.Record("handler");
        publisher.PublishAfterCommit(new OrderCreatedEvent(order.Id));
        return Task.FromResult<Result<Guid>>(order.Id);
    }
}

public sealed class GetOrderHandler(OrderStore store) : IQueryHandler<GetOrderQuery, OrderDto>
{
    public Task<Result<OrderDto>> Handle(GetOrderQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(store.Find(request.OrderId) is { } order && order.CustomerId == request.CustomerId
            ? Result<OrderDto>.Success(order)
            : Result<OrderDto>.Failure(Error.NotFound("PEDIDO_NAO_ENCONTRADO", "Pedido não encontrado.")));
    }
}

public sealed class OrderCreatedHandler(OrderStore store) : INotificationHandler<OrderCreatedEvent>
{
    public Task Handle(OrderCreatedEvent notification, CancellationToken cancellationToken)
    {
        store.Record("notificacao");
        return Task.CompletedTask;
    }
}

/// <summary>Unidade de trabalho em memória: registra begin/commit/rollback.</summary>
public sealed class RecordingUnitOfWork(OrderStore store) : IUnitOfWork
{
    public bool HasActiveTransaction { get; private set; }

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        HasActiveTransaction = true;
        store.Record("begin");
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        HasActiveTransaction = false;
        store.Record("commit");
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        HasActiveTransaction = false;
        store.Record("rollback");
        return Task.CompletedTask;
    }
}

/// <summary>Metadados JSON gerados em compilação para as respostas (sem reflexão do System.Text.Json).</summary>
[JsonSerializable(typeof(ApiResponse<Guid>))]
[JsonSerializable(typeof(ApiResponse<OrderDto>))]
internal sealed partial class AotJsonContext : JsonSerializerContext;
