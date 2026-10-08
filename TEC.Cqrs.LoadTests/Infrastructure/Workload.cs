using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.LoadTests.Infrastructure;

/// <summary>
/// Requisições sintéticas para os cenários que o domínio de pedidos não cobre: handler registrado direto no container
/// (executor criado na primeira chamada), notificações polimórficas, authorizer de interface, milhares de commands
/// internos numa transação e lotes grandes de notificações pós-commit.
/// </summary>
public static class Workload
{
    /// <summary>Container com as requisições sintéticas (registro explícito) e uma unidade de trabalho que conta commits.</summary>
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<WorkloadCounters>();
        services.AddScoped<CountingUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CountingUnitOfWork>());
        services.AddScoped<TestPrincipalAccessor>();
        services.AddScoped<IPrincipalAccessor>(sp => sp.GetRequiredService<TestPrincipalAccessor>());

        services.AddTecCqrs(o => o
            .AddCommandHandler<ProcessBatchCommand, ProcessBatchHandler>()
            .AddCommandHandler<StepCommand, StepHandler>()
            .AddQueryHandler<ReadResourceQuery, string, ReadResourceHandler>()
            .AddRequestAuthorizer<IUserResource, UserResourceAuthorizer>()
            .AddNotificationHandler<LoadNotification, LoadNotificationHandler>()
            .AddNotificationHandler<ILoadNotification, InterfaceNotificationHandler>());

        // Fora do AddTecCqrs: o executor da requisição é criado por reflexão na primeira chamada (disputada nos testes)
        services.AddScoped<IRequestHandler<CountQuery, Result<int>>, CountHandler>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

/// <summary>Contadores do servidor sintético (Singleton).</summary>
public sealed class WorkloadCounters
{
    private long _committedSteps;
    private long _notifications;
    private long _interfaceNotifications;

    public long CommittedSteps => Interlocked.Read(ref _committedSteps);
    public long Notifications => Interlocked.Read(ref _notifications);
    public long InterfaceNotifications => Interlocked.Read(ref _interfaceNotifications);

    internal void CommitSteps(int count) => Interlocked.Add(ref _committedSteps, count);
    internal void NotificationReceived() => Interlocked.Increment(ref _notifications);
    internal void InterfaceNotificationReceived() => Interlocked.Increment(ref _interfaceNotifications);
}

/// <summary>Unidade de trabalho que só conta os passos: confirmados no commit, descartados no rollback.</summary>
public sealed class CountingUnitOfWork(WorkloadCounters counters) : IUnitOfWork
{
    private int? _pending;

    public bool HasActiveTransaction => _pending is not null;

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        _pending = 0;
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        counters.CommitSteps(_pending ?? 0);
        _pending = null;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        _pending = null;
        return Task.CompletedTask;
    }

    internal void RegisterStep() => _pending = (_pending ?? 0) + 1;
}

[AllowAnonymousRequest]
public sealed record CountQuery(int Value) : IQuery<int>;

internal sealed class CountHandler : IQueryHandler<CountQuery, int>
{
    public Task<Result<int>> Handle(CountQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(query.Value + 1);
}

/// <summary>
/// Envia <paramref name="InnerCommands"/> commands internos na mesma transação, publica <paramref name="Notifications"/>
/// notificações após o commit e falha no fim se <paramref name="Fail"/> (tudo é desfeito e descartado).
/// </summary>
[AllowAnonymousRequest, SkipValidation]
public sealed record ProcessBatchCommand(int InnerCommands, int Notifications, bool Fail = false) : ICommand;

internal sealed class ProcessBatchHandler(ISender sender, IPublisher publisher) : ICommandHandler<ProcessBatchCommand>
{
    public async Task<Result> Handle(ProcessBatchCommand command, CancellationToken cancellationToken)
    {
        for (int i = 0; i < command.InnerCommands; i++)
        {
            var result = await sender.Send(new StepCommand(i), cancellationToken);
            if (result.IsFailure)
                return result;
        }

        for (int i = 0; i < command.Notifications; i++)
            publisher.PublishAfterCommit(new LoadNotification(i));

        return command.Fail ? Result.Failure(Error.BusinessRule("LOTE_REJEITADO", "Lote rejeitado.")) : Result.Success();
    }
}

[AllowAnonymousRequest, SkipValidation]
public sealed record StepCommand(int Index) : ICommand;

internal sealed class StepHandler(CountingUnitOfWork unitOfWork) : ICommandHandler<StepCommand>
{
    public Task<Result> Handle(StepCommand command, CancellationToken cancellationToken)
    {
        unitOfWork.RegisterStep();
        return Task.FromResult(Result.Success());
    }
}

public interface ILoadNotification : INotification
{
    int Index { get; }
}

public sealed record LoadNotification(int Index) : ILoadNotification;

internal sealed class LoadNotificationHandler(WorkloadCounters counters) : INotificationHandler<LoadNotification>
{
    public Task Handle(LoadNotification notification, CancellationToken cancellationToken)
    {
        counters.NotificationReceived();
        return Task.CompletedTask;
    }
}

internal sealed class InterfaceNotificationHandler(WorkloadCounters counters) : INotificationHandler<ILoadNotification>
{
    public Task Handle(ILoadNotification notification, CancellationToken cancellationToken)
    {
        counters.InterfaceNotificationReceived();
        return Task.CompletedTask;
    }
}

public interface IUserResource : IBaseRequest
{
    string Owner { get; }
}

/// <summary>Lê um recurso de <paramref name="Owner"/>: só o próprio dono é autorizado (authorizer de interface).</summary>
public sealed record ReadResourceQuery(string Owner) : IQuery<string>, IUserResource;

internal sealed class ReadResourceHandler(IPrincipalAccessor user) : IQueryHandler<ReadResourceQuery, string>
{
    public Task<Result<string>> Handle(ReadResourceQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>($"{query.Owner}:{user.Principal?.Identity?.Name}");
}

internal sealed class UserResourceAuthorizer(IPrincipalAccessor user) : IRequestAuthorizer<IUserResource>
{
    public Task<Result> AuthorizeAsync(IUserResource request, CancellationToken cancellationToken) =>
        Task.FromResult(user.Principal?.Identity?.Name == request.Owner
            ? Result.Success()
            : Result.Failure(Error.NotFound("RECURSO_NAO_ENCONTRADO", "Recurso não encontrado.")));
}
