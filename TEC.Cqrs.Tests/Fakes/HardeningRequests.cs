using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using FluentValidation.Results;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Commands internos em paralelo na mesma transação -----

public enum ChildrenMode
{
    Sequential,
    SequentialWithGrandchild,
    Parallel,
    ParallelHandled
}

[AllowAnonymousRequest, SkipValidation]
public sealed record ParentOfChildrenCommand(ChildrenMode Mode) : ICommand;

internal sealed class ParentOfChildrenHandler(ISender sender, CallLog log) : ICommandHandler<ParentOfChildrenCommand>
{
    public async Task<Result> Handle(ParentOfChildrenCommand command, CancellationToken cancellationToken)
    {
        switch (command.Mode)
        {
            case ChildrenMode.Sequential or ChildrenMode.SequentialWithGrandchild:
                bool grandchild = command.Mode == ChildrenMode.SequentialWithGrandchild;
                if ((await sender.Send(new ChildCommand("A", 10, grandchild), cancellationToken)).IsFailure
                    || (await sender.Send(new ChildCommand("B", 0, grandchild), cancellationToken)).IsFailure)
                {
                    return Result.Failure(Error.BusinessRule("FILHO_FALHOU", "Falhou."));
                }
                break;
            case ChildrenMode.Parallel:
                await Task.WhenAll(
                    sender.Send(new ChildCommand("A", 50), cancellationToken),
                    sender.Send(new ChildCommand("B", 0), cancellationToken));
                break;
            case ChildrenMode.ParallelHandled:
                try
                {
                    await Task.WhenAll(
                        sender.Send(new ChildCommand("A", 50), cancellationToken),
                        sender.Send(new ChildCommand("B", 0), cancellationToken));
                }
                catch (InvalidOperationException)
                {
                    // O handler externo "trata" a rejeição do command concorrente
                }
                break;
        }

        log.Add("pai");
        return Result.Success();
    }
}

[AllowAnonymousRequest, SkipValidation]
public sealed record ChildCommand(string Name, int DelayMs, bool SendGrandchild = false) : ICommand;

internal sealed class ChildHandler(ISender sender, CallLog log) : ICommandHandler<ChildCommand>
{
    public async Task<Result> Handle(ChildCommand command, CancellationToken cancellationToken)
    {
        log.Add($"filho:{command.Name}");
        await Task.Delay(command.DelayMs, cancellationToken);
        if (command.SendGrandchild)
            return await sender.Send(new ChildCommand($"{command.Name}.neto", 0), cancellationToken);
        return Result.Success();
    }
}

// ----- PublishAfterCommit em ciclo -----

[AllowAnonymousRequest, SkipValidation]
public sealed record PublishCycleCommand : ICommand;

internal sealed class PublishCycleHandler(IPublisher publisher) : ICommandHandler<PublishCycleCommand>
{
    public Task<Result> Handle(PublishCycleCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new CycleEvent(1));
        return Task.FromResult(Result.Success());
    }
}

public sealed record CycleEvent(int Round) : INotification;

/// <summary>Handler pós-commit que publica de novo (ciclo sem fim, até o limite de rodadas).</summary>
internal sealed class CycleEventHandler(IPublisher publisher, CallLog log) : INotificationHandler<CycleEvent>
{
    public Task Handle(CycleEvent notification, CancellationToken cancellationToken)
    {
        log.Add($"ciclo:{notification.Round}");
        publisher.PublishAfterCommit(new CycleEvent(notification.Round + 1));
        return Task.CompletedTask;
    }
}

// ----- Handler de query que retorna null -----

[AllowAnonymousRequest]
public sealed record ReturnsNullQuery(bool Async) : IQuery<int>;

internal sealed class ReturnsNullQueryHandler : IQueryHandler<ReturnsNullQuery, int>
{
    public async Task<Result<int>> Handle(ReturnsNullQuery query, CancellationToken cancellationToken)
    {
        if (query.Async)
            await Task.Yield();
        return null!;
    }
}

/// <summary>Behavior próprio que retorna null em vez de chamar o próximo passo.</summary>
internal sealed class ReturnsNullBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) =>
        Task.FromResult<TResponse>(null!);
}

// ----- Exceção convertida por IExceptionErrorMapper -----

/// <summary>Converte <see cref="TimeoutException"/> em erro interno (não exposto ao cliente).</summary>
internal sealed class TimeoutErrorMapper : IExceptionErrorMapper
{
    public bool TryMap(Exception exception, [NotNullWhen(true)] out IReadOnlyList<Error>? errors)
    {
        errors = exception is TimeoutException ? [Error.Failure("TIMEOUT_MAPEADO", "Serviço indisponível.")] : null;
        return errors is not null;
    }
}

// ----- FluentValidation com regras assíncronas -----

[AllowAnonymousRequest]
public sealed record ReserveNicknameCommand(string Nickname) : ICommand;

internal sealed class ReserveNicknameHandler(CallLog log) : ICommandHandler<ReserveNicknameCommand>
{
    public Task<Result> Handle(ReserveNicknameCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class ReserveNicknameValidator : AbstractValidator<ReserveNicknameCommand>
{
    public ReserveNicknameValidator()
    {
        RuleFor(c => c.Nickname)
            .MustAsync(async (nickname, ct) =>
            {
                await Task.Delay(1, ct);
                return !string.Equals(nickname, "admin", StringComparison.OrdinalIgnoreCase);
            })
            .WithErrorCode("APELIDO_RESERVADO")
            .WithMessage("Apelido reservado.");

        RuleFor(c => c.Nickname).CustomAsync(async (nickname, context, ct) =>
        {
            await Task.Yield();
            if (nickname.Length > 10)
                context.AddFailure(new ValidationFailure("Nickname", "Apelido longo demais.") { ErrorCode = "APELIDO_LONGO" });
        });
    }
}

// ----- Outro IPrincipalAccessor (ex.: identidade de um job) -----

internal sealed class JobUserAccessor : IPrincipalAccessor
{
    public System.Security.Claims.ClaimsPrincipal? Principal => FakePrincipalAccessor.User("job");
}
