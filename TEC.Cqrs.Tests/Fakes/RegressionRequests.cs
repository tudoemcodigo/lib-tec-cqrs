using System.Collections.Concurrent;
using FluentValidation;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Validação: dois validators para a mesma requisição -----

[AllowAnonymousRequest]
public sealed record TwoValidatorsCommand(string Name, string Cpf) : ICommand;

internal sealed class TwoValidatorsHandler : ICommandHandler<TwoValidatorsCommand>
{
    public Task<Result> Handle(TwoValidatorsCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class TwoValidatorsNameValidator : AbstractValidator<TwoValidatorsCommand>
{
    public TwoValidatorsNameValidator() => RuleFor(c => c.Name).NotEmpty().WithErrorCode("NOME_OBRIGATORIO");
}

internal sealed class TwoValidatorsCpfValidator : AbstractValidator<TwoValidatorsCommand>
{
    public TwoValidatorsCpfValidator() => RuleFor(c => c.Cpf).NotEmpty().WithErrorCode("CPF_OBRIGATORIO");
}

// ----- Autorização para tipo base / interface -----

/// <summary>Interface compartilhada por requisições de pedidos do cliente (autorizada por um único authorizer).</summary>
public interface ICustomerOrder : IBaseRequest
{
    Guid CustomerId { get; }
}

[SkipValidation]
public sealed record CancelOrderCommand(Guid CustomerId) : ICommand, ICustomerOrder;

internal sealed class CancelOrderHandler(CallLog log) : ICommandHandler<CancelOrderCommand>
{
    public Task<Result> Handle(CancelOrderCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class CancelOrderAuthorizer(CallLog log) : IRequestAuthorizer<CancelOrderCommand>
{
    public Task<Result> AuthorizeAsync(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        log.Add("authorizer:concreto");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class CustomerOrderAuthorizer(CallLog log) : IRequestAuthorizer<ICustomerOrder>
{
    public Task<Result> AuthorizeAsync(ICustomerOrder request, CancellationToken cancellationToken)
    {
        log.Add("authorizer:interface");
        return Task.FromResult(request.CustomerId == Guid.Empty
            ? Result.Failure(Error.Forbidden("PEDIDO_DE_OUTRO_CLIENTE", "Acesso negado."))
            : Result.Success());
    }
}

/// <summary>Classe base autorizada por um authorizer próprio.</summary>
public abstract record AuditedCommand : ICommand;

[SkipValidation]
public sealed record DeleteAccountCommand(bool Allowed) : AuditedCommand;

internal sealed class DeleteAccountHandler(CallLog log) : ICommandHandler<DeleteAccountCommand>
{
    public Task<Result> Handle(DeleteAccountCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class AuditedCommandAuthorizer : IRequestAuthorizer<AuditedCommand>
{
    public Task<Result> AuthorizeAsync(AuditedCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(request is DeleteAccountCommand { Allowed: true }
            ? Result.Success()
            : Result.Failure(Error.Forbidden("NAO_AUDITADO", "Acesso negado.")));
}

/// <summary>Sem autorização declarada (genérica aberta: fora da varredura; fechada só nos testes).</summary>
public sealed record NoAuthorizationQuery<T> : IQuery<int>;

internal sealed class NoAuthorizationHandler<T> : IQueryHandler<NoAuthorizationQuery<T>, int>
{
    public Task<Result<int>> Handle(NoAuthorizationQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

// ----- Notificações para tipo base / interface -----

public interface ICustomerEvent : INotification;

public sealed record CustomerUpdatedEvent(Guid Id) : ICustomerEvent;

internal sealed class CustomerUpdatedHandler(CallLog log) : INotificationHandler<CustomerUpdatedEvent>
{
    public Task Handle(CustomerUpdatedEvent notification, CancellationToken cancellationToken)
    {
        log.Add("atualizado");
        return Task.CompletedTask;
    }
}

internal sealed class CustomerAuditHandler(CallLog log) : INotificationHandler<ICustomerEvent>
{
    public Task Handle(ICustomerEvent notification, CancellationToken cancellationToken)
    {
        log.Add("auditoria");
        return Task.CompletedTask;
    }
}

/// <summary>Trata o evento concreto e a interface: deve rodar uma única vez (pelo tipo mais específico).</summary>
internal sealed class DualHandler(CallLog log) : INotificationHandler<CustomerUpdatedEvent>, INotificationHandler<ICustomerEvent>
{
    public Task Handle(CustomerUpdatedEvent notification, CancellationToken cancellationToken)
    {
        log.Add("duplo:concreto");
        return Task.CompletedTask;
    }

    public Task Handle(ICustomerEvent notification, CancellationToken cancellationToken)
    {
        log.Add("duplo:interface");
        return Task.CompletedTask;
    }
}

/// <summary>Evento cujo handler concreto não pode ser criado (registrado com factory que lança, nos testes).</summary>
public sealed record EventWithBrokenHandler : ICustomerEvent;

[AllowAnonymousRequest, SkipValidation]
public sealed record PublishBrokenEventCommand : ICommand;

internal sealed class PublishBrokenEventHandler(IPublisher publisher) : ICommandHandler<PublishBrokenEventCommand>
{
    public Task<Result> Handle(PublishBrokenEventCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new EventWithBrokenHandler());
        return Task.FromResult(Result.Success());
    }
}

// ----- Transação: falha de command aninhado -----

public enum ChildFailureMode
{
    Failure,
    Exception,
    Validation,
    QueryNotFound,
    NoTransactionFailure,
    NoTransactionException
}

/// <summary>Command com <c>[SkipTransaction]</c> que falha (enviado de dentro de um command com transação).</summary>
[SkipTransaction, SkipValidation]
[AllowAnonymousRequest]
public sealed record FailingNoTransactionCommand(bool Throw = false) : ICommand;

internal sealed class FailingNoTransactionHandler : ICommandHandler<FailingNoTransactionCommand>
{
    public Task<Result> Handle(FailingNoTransactionCommand command, CancellationToken cancellationToken) =>
        command.Throw
            ? throw new InvalidOperationException("falha no command sem transação")
            : Task.FromResult(Result.Failure(Error.BusinessRule("FALHOU_SEM_TRANSACAO", "Falhou.")));
}

/// <summary>Handler que retorna null (bug do handler): a transação não pode ficar aberta.</summary>
[AllowAnonymousRequest, SkipValidation]
public sealed record ReturnsNullCommand : ICommand;

internal sealed class ReturnsNullHandler : ICommandHandler<ReturnsNullCommand>
{
    public Task<Result> Handle(ReturnsNullCommand command, CancellationToken cancellationToken) => Task.FromResult<Result>(null!);
}

/// <summary>Command que envia um command/query interno que falha e ignora a falha (retorna sucesso).</summary>
[AllowAnonymousRequest, SkipValidation]
public sealed record ParentIgnoresChildFailureCommand(ChildFailureMode Mode) : ICommand;

internal sealed class ParentIgnoresChildFailureHandler(ISender sender, IPublisher publisher, CallLog log)
    : ICommandHandler<ParentIgnoresChildFailureCommand>
{
    public async Task<Result> Handle(ParentIgnoresChildFailureCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new CustomerCreatedEvent(Guid.NewGuid()));

        switch (command.Mode)
        {
            case ChildFailureMode.Failure:
                await sender.Send(new CreateWithEventCommand(Fail: true), cancellationToken);
                break;
            case ChildFailureMode.Exception:
                try
                {
                    await sender.Send(new CreateWithEventCommand(Throw: true), cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // O handler externo "trata" a exceção do interno
                }
                break;
            case ChildFailureMode.Validation:
                await sender.Send(new CreateCustomerCommand("", ""), cancellationToken);
                break;
            case ChildFailureMode.QueryNotFound:
                await sender.Send(new GetCustomerQuery(Guid.Empty), cancellationToken);
                break;
            case ChildFailureMode.NoTransactionFailure:
                await sender.Send(new FailingNoTransactionCommand(), cancellationToken);
                break;
            case ChildFailureMode.NoTransactionException:
                try
                {
                    await sender.Send(new FailingNoTransactionCommand(Throw: true), cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // O handler externo "trata" a exceção do interno
                }
                break;
        }

        log.Add("pai");
        return Result.Success();
    }
}

// ----- Concorrência no mesmo escopo -----

[AllowAnonymousRequest, SkipValidation]
public sealed record ParallelCommand(string Name, int DelayMs, bool Fail = false) : ICommand;

internal sealed class ParallelHandler(IPublisher publisher) : ICommandHandler<ParallelCommand>
{
    public async Task<Result> Handle(ParallelCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new ParallelEvent(command.Name));
        await Task.Delay(command.DelayMs, cancellationToken);
        return command.Fail ? Result.Failure(Error.BusinessRule("FALHOU", "Falhou.")) : Result.Success();
    }
}

public sealed record ParallelEvent(string Name) : INotification;

internal sealed class ParallelEventHandler(CallLog log) : INotificationHandler<ParallelEvent>
{
    public Task Handle(ParallelEvent notification, CancellationToken cancellationToken)
    {
        log.Add($"evento:{notification.Name}");
        return Task.CompletedTask;
    }
}

/// <summary>Behavior que chama PublishAfterCommit antes do handler (fora do TransactionBehavior).</summary>
internal sealed class PublishInBehavior<TRequest, TResponse>(IPublisher publisher) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new CustomerCreatedEvent(Guid.NewGuid()));
        return next(cancellationToken);
    }
}

// ----- Logging / performance -----

[AllowAnonymousRequest, SkipValidation]
public sealed record ParentOfFailCommand : ICommand;

internal sealed class ParentOfFailHandler(ISender sender) : ICommandHandler<ParentOfFailCommand>
{
    public Task<Result> Handle(ParentOfFailCommand command, CancellationToken cancellationToken) =>
        sender.Send(new FailCommand(), cancellationToken);
}

[AllowAnonymousRequest]
public sealed record CancellableQuery : IQuery<int>;

internal sealed class CancellableHandler : IQueryHandler<CancellableQuery, int>
{
    public Task<Result<int>> Handle(CancellableQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Result<int>>(1);
    }
}

[AllowAnonymousRequest, SkipValidation]
public sealed record SlowWithExceptionCommand : ICommand;

internal sealed class SlowWithExceptionHandler : ICommandHandler<SlowWithExceptionCommand>
{
    public async Task<Result> Handle(SlowWithExceptionCommand command, CancellationToken cancellationToken)
    {
        await Task.Delay(30, cancellationToken);
        throw new TimeoutException("timeout no banco");
    }
}

/// <summary>Logger que guarda os registros para inspeção nos testes.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class ListLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
