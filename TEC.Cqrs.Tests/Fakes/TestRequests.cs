#if NET9_0_OR_GREATER
using SyncLock = System.Threading.Lock;
#else
using SyncLock = object;
#endif
using FluentValidation;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Core.Responses.Pagination;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Commands / queries usados nos testes (registrados por varredura do assembly de testes) -----

[AllowAnonymousRequest]
public sealed record CreateCustomerCommand(string Name, string Cpf) : ICommand<Guid>;

internal sealed class CreateCustomerHandler(CallLog log) : ICommandHandler<CreateCustomerCommand, Guid>
{
    public static readonly Guid CreatedId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public Task<Result<Guid>> Handle(CreateCustomerCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult<Result<Guid>>(CreatedId);
    }
}

internal sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithErrorCode("NOME_OBRIGATORIO").WithMessage("Nome é obrigatório.");
        RuleFor(c => c.Cpf).Length(11).WithErrorCode("CPF_INVALIDO").WithMessage("CPF inválido.");
    }
}

[SkipValidation]
[AllowAnonymousRequest]
public sealed record DeactivateCustomerCommand(Guid Id) : ICommand;

internal sealed class DeactivateCustomerHandler : ICommandHandler<DeactivateCustomerCommand>
{
    public Task<Result> Handle(DeactivateCustomerCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(command.Id == Guid.Empty
            ? Result.Failure(Error.BusinessRule("CLIENTE_JA_INATIVO", "Cliente já está inativo."))
            : Result.Success());
}

[AllowAnonymousRequest]
public sealed record GetCustomerQuery(Guid Id) : IQuery<string>;

internal sealed class GetCustomerHandler : IQueryHandler<GetCustomerQuery, string>
{
    public Task<Result<string>> Handle(GetCustomerQuery query, CancellationToken cancellationToken) =>
        query.Id == Guid.Empty
            ? throw new NotFoundException("CLIENTE_NAO_ENCONTRADO", "Cliente não encontrado.")
            : Task.FromResult<Result<string>>("Maria");
}

[AllowAnonymousRequest]
public sealed record ListCustomersQuery(int Page) : IQuery<PagedResult<string>>;

internal sealed class ListCustomersHandler : IQueryHandler<ListCustomersQuery, PagedResult<string>>
{
    public Task<Result<PagedResult<string>>> Handle(ListCustomersQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<PagedResult<string>>>(new PagedResult<string>(["Ana", "Bia"], query.Page, 2, 5));
}

/// <summary>Command que lança uma exceção qualquer (não AppException).</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record FailCommand : ICommand;

internal sealed class FailHandler : ICommandHandler<FailCommand>
{
    public Task<Result> Handle(FailCommand command, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("detalhe interno: connection string xyz");
}

/// <summary>Command que lança exceção de integração (mensagem nunca exposta ao cliente).</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record IntegrateCommand : ICommand;

internal sealed class IntegrateHandler : ICommandHandler<IntegrateCommand>
{
    public Task<Result> Handle(IntegrateCommand command, CancellationToken cancellationToken) =>
        throw new IntegrationException("Pagamentos", "Gateway retornou 503 em https://interno");
}

[SkipTransaction, SkipValidation]
[AllowAnonymousRequest]
public sealed record NoTransactionCommand : ICommand;

internal sealed class NoTransactionHandler : ICommandHandler<NoTransactionCommand>
{
    public Task<Result> Handle(NoTransactionCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

/// <summary>Command que envia outro command (transação aninhada).</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record ParentCommand : ICommand;

internal sealed class ParentCommandHandler(ISender sender) : ICommandHandler<ParentCommand>
{
    public async Task<Result> Handle(ParentCommand command, CancellationToken cancellationToken)
    {
        var child = await sender.Send(new CreateCustomerCommand("Filho", "12345678909"), cancellationToken);
        return child.IsSuccess ? Result.Success() : child.ToFailure();
    }
}

/// <summary>Requisição propositalmente sem handler.</summary>
[AllowAnonymousRequest]
public sealed record NoHandlerQuery : IQuery<int>;

// ----- Notificações -----

public sealed record CustomerCreatedEvent(Guid Id) : INotification;

internal sealed class EmailSenderHandler(CallLog log) : INotificationHandler<CustomerCreatedEvent>
{
    public Task Handle(CustomerCreatedEvent notification, CancellationToken cancellationToken)
    {
        log.Add("email");
        return Task.CompletedTask;
    }
}

internal sealed class CrmSyncHandler(CallLog log) : INotificationHandler<CustomerCreatedEvent>
{
    public Task Handle(CustomerCreatedEvent notification, CancellationToken cancellationToken)
    {
        log.Add("crm");
        return Task.CompletedTask;
    }
}

// ----- Behavior próprio -----

internal sealed class RecordingBehavior<TRequest, TResponse>(CallLog log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        log.Add("behavior:antes");
        var response = await next(cancellationToken);
        log.Add("behavior:depois");
        return response;
    }
}

// ----- Infra -----

/// <summary>Registro da sequência de chamadas (Scoped por teste; seguro para Sends em paralelo).</summary>
public sealed class CallLog
{
    private readonly SyncLock _lock = new();
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
                return [.. _entries];
        }
    }

    public void Add(string entry)
    {
        lock (_lock)
            _entries.Add(entry);
    }
}

/// <summary>Unit of work falso que registra as chamadas no <see cref="CallLog"/>.</summary>
public sealed class FakeUnitOfWork(CallLog log) : IUnitOfWork
{
    public bool HasActiveTransaction { get; private set; }

    public bool FailOnCommit { get; set; }

    public bool FailOnRollback { get; set; }

    /// <summary>Token recebido no último commit.</summary>
    public CancellationToken CommitToken { get; private set; }

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        HasActiveTransaction = true;
        log.Add("begin");
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        CommitToken = cancellationToken;
        if (FailOnCommit)
            throw new InvalidOperationException("falha no commit");
        HasActiveTransaction = false;
        log.Add("commit");
        return Task.CompletedTask;
    }

    /// <summary>Encerra a transação sem passar pelo pipeline (ex.: rollback do banco após deadlock).</summary>
    public void EndOutsidePipeline() => HasActiveTransaction = false;

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        HasActiveTransaction = false;
        log.Add("rollback");
        if (FailOnRollback)
            throw new InvalidOperationException("falha no rollback");
        return Task.CompletedTask;
    }
}
