using System.Security.Claims;
using System.Text.Json.Serialization;
using FluentValidation;
using TEC.Core.Common.Results;
using TEC.Core.Responses.Pagination;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.SampleApi;

// ----- Contratos -----

/// <summary>Pedido devolvido pela API.</summary>
public sealed record OrderDto(
    Guid Id,
    [property: JsonPropertyName("clienteId")] string CustomerId,
    [property: JsonPropertyName("descricao")] string Description,
    [property: JsonPropertyName("valor")] decimal Amount,
    [property: JsonPropertyName("estornado")] bool Refunded)
{
    internal static OrderDto From(Order order) => new(order.Id, order.CustomerId, order.Description, order.Amount, order.Refunded);
}

/// <summary>Verificação de saúde (pública).</summary>
[AllowAnonymousRequest]
public sealed record GetHealthQuery : IQuery<string>;

/// <summary>Cria um pedido para o usuário autenticado (policy <c>PedidosEscrita</c>: papel Cliente ou Admin).</summary>
[AuthorizeRequest(Policy = SampleApiApp.WritePolicy)]
public sealed record CreateOrderCommand(
    [property: JsonPropertyName("descricao")] string? Description,
    [property: JsonPropertyName("valor")] decimal Amount) : ICommand<Guid>;

/// <summary>
/// Cria vários pedidos, cada um por um <see cref="CreateOrderCommand"/> interno na mesma transação: se um item for
/// inválido, nenhum é gravado e nenhuma notificação é publicada.
/// </summary>
[AuthorizeRequest(Policy = SampleApiApp.WritePolicy)]
public sealed record ImportOrdersCommand([property: JsonPropertyName("itens")] IReadOnlyList<CreateOrderCommand>? Items) : ICommand<int>;

/// <summary>Obtém um pedido do próprio usuário (o authorizer devolve 404 para pedidos de outros clientes).</summary>
[AuthorizeRequest]
public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;

/// <summary>Lista os pedidos do próprio usuário.</summary>
[AuthorizeRequest]
public sealed record ListOrdersQuery(int Page, int PageSize) : IQuery<PagedResult<OrderDto>>;

/// <summary>Estorna qualquer pedido (somente papel Admin).</summary>
[AuthorizeRequest(Roles = SampleApiApp.AdminRole)]
public sealed record RefundOrderCommand(Guid OrderId) : ICommand;

/// <summary>
/// Lança uma exceção com um texto interno: a resposta precisa ser 500 genérico, sem a mensagem nem o stack trace.
/// </summary>
[AuthorizeRequest, SkipValidation]
public sealed record SimulateFailureCommand : ICommand;

/// <summary>Pedido criado (publicado somente após o commit).</summary>
public sealed record OrderCreatedEvent(Guid OrderId, string CustomerId) : INotification;

// ----- Validação -----

internal sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(c => c.Description).NotEmpty().WithErrorCode("DESCRICAO_OBRIGATORIA").WithMessage("Descrição é obrigatória.")
            .MaximumLength(200).WithErrorCode("DESCRICAO_LONGA").WithMessage("A descrição deve ter no máximo 200 caracteres.")
            .OverridePropertyName("descricao");
        RuleFor(c => c.Amount).GreaterThan(0).WithErrorCode("VALOR_INVALIDO").WithMessage("O valor deve ser maior que zero.")
            .LessThanOrEqualTo(1_000_000).WithErrorCode("VALOR_INVALIDO").WithMessage("O valor deve ser de no máximo 1.000.000.")
            .OverridePropertyName("valor");
    }
}

internal sealed class ImportOrdersValidator : AbstractValidator<ImportOrdersCommand>
{
    public ImportOrdersValidator() =>
        RuleFor(c => c.Items).NotEmpty().WithErrorCode("ITENS_OBRIGATORIOS").WithMessage("Informe ao menos um item.")
            .Must(i => i is null || i.Count <= 100).WithErrorCode("ITENS_DEMAIS").WithMessage("Informe no máximo 100 itens.")
            .OverridePropertyName("itens");
}

internal sealed class ListOrdersValidator : AbstractValidator<ListOrdersQuery>
{
    public ListOrdersValidator()
    {
        RuleFor(q => q.Page).InclusiveBetween(1, 10_000).WithErrorCode("PAGINA_INVALIDA").WithMessage("Página inválida.")
            .OverridePropertyName("pagina");
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100).WithErrorCode("TAMANHO_INVALIDO").WithMessage("Informe de 1 a 100 itens por página.")
            .OverridePropertyName("tamanho");
    }
}

internal sealed class RefundOrderValidator : IRequestValidator<RefundOrderCommand>
{
    public Task<Result> ValidateAsync(RefundOrderCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(command.OrderId == Guid.Empty
            ? Result.Failure(Error.Validation("PEDIDO_OBRIGATORIO", "Informe o pedido.", "pedidoId"))
            : Result.Success());
}

// ----- Autorização sobre o recurso -----

/// <summary>Só o dono vê o pedido. Pedido de outro cliente e pedido inexistente dão o mesmo 404 (não revela que existe).</summary>
internal sealed class GetOrderAuthorizer(IPrincipalAccessor user, OrderStore store) : IRequestAuthorizer<GetOrderQuery>
{
    public Task<Result> AuthorizeAsync(GetOrderQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(store.Get(query.OrderId) is { } order && order.CustomerId == user.CustomerId()
            ? Result.Success()
            : Result.Failure(OrderHandlers.OrderNotFound));
}

// ----- Handlers -----

internal static class OrderHandlers
{
    public static readonly Error OrderNotFound = Error.NotFound("PEDIDO_NAO_ENCONTRADO", "Pedido não encontrado.");

    /// <summary>Id do cliente do usuário autenticado (claim <c>cliente_id</c>).</summary>
    public static string CustomerId(this IPrincipalAccessor user) =>
        user.Principal?.FindFirstValue(SampleAuthentication.CustomerClaim)
        ?? throw new InvalidOperationException("Usuário sem cliente_id: a autorização deveria ter barrado a requisição.");
}

internal sealed class GetHealthHandler : IQueryHandler<GetHealthQuery, string>
{
    public Task<Result<string>> Handle(GetHealthQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<string>>("ok");
}

internal sealed class CreateOrderHandler(IPrincipalAccessor user, InMemoryUnitOfWork unitOfWork, IPublisher publisher, SampleMetrics metrics)
    : ICommandHandler<CreateOrderCommand, Guid>
{
    public Task<Result<Guid>> Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        metrics.HandlerExecuted(nameof(CreateOrderHandler));
        var order = new Order(Guid.NewGuid(), user.CustomerId(), command.Description!, command.Amount);
        unitOfWork.Register(store => store.Add(order));
        publisher.PublishAfterCommit(new OrderCreatedEvent(order.Id, order.CustomerId));
        return Task.FromResult<Result<Guid>>(order.Id);
    }
}

internal sealed class ImportOrdersHandler(ISender sender, SampleMetrics metrics) : ICommandHandler<ImportOrdersCommand, int>
{
    public async Task<Result<int>> Handle(ImportOrdersCommand command, CancellationToken cancellationToken)
    {
        metrics.HandlerExecuted(nameof(ImportOrdersHandler));

        // Em sequência: commands internos participam da transação do externo, um por vez
        foreach (var item in command.Items!)
        {
            var result = await sender.Send(item, cancellationToken);
            if (result.IsFailure)
                return result.ToFailure<int>();
        }

        return command.Items!.Count;
    }
}

internal sealed class GetOrderHandler(OrderStore store, SampleMetrics metrics) : IQueryHandler<GetOrderQuery, OrderDto>
{
    public Task<Result<OrderDto>> Handle(GetOrderQuery query, CancellationToken cancellationToken)
    {
        metrics.HandlerExecuted(nameof(GetOrderHandler));
        return Task.FromResult(store.Get(query.OrderId) is { } order
            ? Result<OrderDto>.Success(OrderDto.From(order))
            : Result<OrderDto>.Failure(OrderHandlers.OrderNotFound));
    }
}

internal sealed class ListOrdersHandler(IPrincipalAccessor user, OrderStore store, SampleMetrics metrics)
    : IQueryHandler<ListOrdersQuery, PagedResult<OrderDto>>
{
    public Task<Result<PagedResult<OrderDto>>> Handle(ListOrdersQuery query, CancellationToken cancellationToken)
    {
        metrics.HandlerExecuted(nameof(ListOrdersHandler));
        var (items, total) = store.ListPage(user.CustomerId(), query.Page, query.PageSize);
        return Task.FromResult<Result<PagedResult<OrderDto>>>(
            new PagedResult<OrderDto>([.. items.Select(OrderDto.From)], query.Page, query.PageSize, total));
    }
}

internal sealed class RefundOrderHandler(OrderStore store, InMemoryUnitOfWork unitOfWork, SampleMetrics metrics)
    : ICommandHandler<RefundOrderCommand>
{
    public Task<Result> Handle(RefundOrderCommand command, CancellationToken cancellationToken)
    {
        metrics.HandlerExecuted(nameof(RefundOrderHandler));
        if (store.Get(command.OrderId) is null)
            return Task.FromResult(Result.Failure(OrderHandlers.OrderNotFound));

        unitOfWork.Register(s => s.Refund(command.OrderId));
        return Task.FromResult(Result.Success());
    }
}

internal sealed class SimulateFailureHandler(SampleMetrics metrics) : ICommandHandler<SimulateFailureCommand>
{
    /// <summary>Texto que nunca pode aparecer na resposta HTTP.</summary>
    public const string InternalSecret = "segredo-interno-que-nao-pode-vazar";

    public Task<Result> Handle(SimulateFailureCommand command, CancellationToken cancellationToken)
    {
        metrics.HandlerExecuted(nameof(SimulateFailureHandler));
        throw new InvalidOperationException($"Falha simulada com dado interno: {InternalSecret}");
    }
}

internal sealed class OrderCreatedHandler(SampleMetrics metrics) : INotificationHandler<OrderCreatedEvent>
{
    public Task Handle(OrderCreatedEvent notification, CancellationToken cancellationToken)
    {
        metrics.OrderCreated();
        return Task.CompletedTask;
    }
}
