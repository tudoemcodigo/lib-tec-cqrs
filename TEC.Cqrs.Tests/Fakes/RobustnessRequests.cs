using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Commands internos com [SkipTransaction] na transação de outro command -----

public enum ChildrenWithoutTransactionMode
{
    Sequential,
    Parallel,
    WithTransactionalGrandchild
}

[AllowAnonymousRequest, SkipValidation]
public sealed record ParentOfChildrenWithoutTransactionCommand(ChildrenWithoutTransactionMode Mode) : ICommand;

internal sealed class ParentOfChildrenWithoutTransactionHandler(ISender sender, CallLog log) : ICommandHandler<ParentOfChildrenWithoutTransactionCommand>
{
    public async Task<Result> Handle(ParentOfChildrenWithoutTransactionCommand command, CancellationToken cancellationToken)
    {
        switch (command.Mode)
        {
            case ChildrenWithoutTransactionMode.Sequential:
                if ((await sender.Send(new ChildWithoutTransactionCommand("A", 10), cancellationToken)).IsFailure
                    || (await sender.Send(new ChildWithoutTransactionCommand("B", 0), cancellationToken)).IsFailure)
                {
                    return Result.Failure(Error.BusinessRule("FILHO_FALHOU", "Falhou."));
                }
                break;
            case ChildrenWithoutTransactionMode.Parallel:
                await Task.WhenAll(
                    sender.Send(new ChildWithoutTransactionCommand("A", 50), cancellationToken),
                    sender.Send(new ChildWithoutTransactionCommand("B", 0), cancellationToken));
                break;
            case ChildrenWithoutTransactionMode.WithTransactionalGrandchild:
                if ((await sender.Send(new ChildWithoutTransactionCommand("A", 0, SendGrandchild: true), cancellationToken)).IsFailure)
                    return Result.Failure(Error.BusinessRule("FILHO_FALHOU", "Falhou."));
                break;
        }

        log.Add("pai");
        return Result.Success();
    }
}

[SkipTransaction, AllowAnonymousRequest, SkipValidation]
public sealed record ChildWithoutTransactionCommand(string Name, int DelayMs, bool SendGrandchild = false) : ICommand;

internal sealed class ChildWithoutTransactionHandler(ISender sender, CallLog log) : ICommandHandler<ChildWithoutTransactionCommand>
{
    public async Task<Result> Handle(ChildWithoutTransactionCommand command, CancellationToken cancellationToken)
    {
        log.Add($"semtx:{command.Name}");
        await Task.Delay(command.DelayMs, cancellationToken);
        if (command.SendGrandchild)
            return await sender.Send(new ChildCommand($"{command.Name}.neto", 0), cancellationToken);
        return Result.Success();
    }
}

// ----- Transação do command externo encerrada fora do pipeline -----

[AllowAnonymousRequest, SkipValidation]
public sealed record LosesTransactionCommand : ICommand;

/// <summary>Encerra a transação direto no IUnitOfWork (como um rollback do banco após deadlock) e envia um command interno.</summary>
internal sealed class LosesTransactionHandler(IServiceProvider serviceProvider, ISender sender) : ICommandHandler<LosesTransactionCommand>
{
    public async Task<Result> Handle(LosesTransactionCommand command, CancellationToken cancellationToken)
    {
        serviceProvider.GetRequiredService<FakeUnitOfWork>().EndOutsidePipeline();
        return await sender.Send(new ChildCommand("A", 0), cancellationToken);
    }
}

// ----- Authorizers genéricos abertos -----

public interface ITenantScoped;

/// <summary>Genérica para ficar fora da varredura (sem autorização declarada).</summary>
public sealed record TenantQuery<T> : IQuery<int>, ITenantScoped;

internal sealed class TenantQueryHandler<T> : IQueryHandler<TenantQuery<T>, int>
{
    public Task<Result<int>> Handle(TenantQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Authorizer genérico aberto com restrição: o container só o aplica às requisições <see cref="IDoTenant"/>.</summary>
internal sealed class TenantAuthorizer<TRequest>(CallLog log) : IRequestAuthorizer<TRequest>
    where TRequest : IBaseRequest, ITenantScoped
{
    public Task<Result> AuthorizeAsync(TRequest request, CancellationToken cancellationToken)
    {
        log.Add("tenant");
        return Task.FromResult(Result.Success());
    }
}
