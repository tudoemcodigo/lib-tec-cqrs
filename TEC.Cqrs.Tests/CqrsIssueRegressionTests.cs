using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using Microsoft.Extensions.Hosting;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Behaviors;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Tests.Fakes;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests;

public class CqrsIssueRegressionTests
{
    public static readonly List<string> PublishedEvents = [];

    // Issue #2: Falha ou exceção em behavior próprio pós-commit não descarta eventos pós-commit
    [Test]
    public async Task PostCommitBehaviorFailure_DoesNotDiscardDeferredNotifications()
    {
        PublishedEvents.Clear();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddScoped<IUnitOfWork, FakeUnitOfWork>();
        services.AddTecCqrs(opt =>
        {
            opt.RegisterServicesFromAssemblyContaining<CqrsIssueRegressionTests>();
            opt.AddBehavior(typeof(FailingPostCommitBehavior<,>));
        });

        var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        var result = await mediator.Send(new CommandWithDeferredEvent());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(PublishedEvents).Contains("EventFired");
    }

    public class FakeUnitOfWork : IUnitOfWork
    {
        public bool HasActiveTransaction { get; set; }
        public Task BeginTransactionAsync(CancellationToken cancellationToken)
        {
            HasActiveTransaction = true;
            return Task.CompletedTask;
        }
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [AllowAnonymousRequest]
    [SkipValidation]
    public record CommandWithDeferredEvent : ICommand;

    public class CommandWithDeferredEventHandler(IPublisher publisher) : ICommandHandler<CommandWithDeferredEvent>
    {
        public Task<Result> Handle(CommandWithDeferredEvent request, CancellationToken cancellationToken)
        {
            publisher.PublishAfterCommit(new DeferredTestNotification());
            return Task.FromResult(Result.Success());
        }
    }

    public record DeferredTestNotification : INotification;

    public class DeferredTestNotificationHandler : INotificationHandler<DeferredTestNotification>
    {
        public Task Handle(DeferredTestNotification notification, CancellationToken cancellationToken)
        {
            PublishedEvents.Add("EventFired");
            return Task.CompletedTask;
        }
    }

    public class FailingPostCommitBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
        where TResponse : Result
    {
        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            var res = await next(cancellationToken);
            if (request is CommandWithDeferredEvent)
            {
                return (TResponse)(object)Result.Failure(Error.Conflict("FAIL", "Post-commit failure"));
            }
            return res;
        }
    }

    // Issue #3: authorizer de interface base. Antes, este teste varria o assembly (que já registra o BaseAuthorizer pelo
    // AddTecCqrs) e passava pela varredura, não pelo registro direto no container que o nome dizia testar
    [Test]
    public async Task BaseInterfaceAuthorizer_RegisteredThroughAddTecCqrs_IsExecuted()
    {
        // Sem varredura: o único caminho do authorizer é o AddRequestAuthorizer da interface
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecCqrs(opt => opt
            .AddCommandHandler<ConcreteEntityRequest, ConcreteEntityRequestHandler>()
            .AddRequestAuthorizer<IAuthorizedEntityRequest, BaseAuthorizer>()
            .AuthorizersRequireAuthentication = false);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ConcreteEntityRequest());

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Errors[0].Code).IsEqualTo("AUTH_DENIED");
    }

    [Test]
    public async Task BaseInterfaceAuthorizer_RegisteredDirectlyInContainerAfterAddTecCqrs_FailsClosedOnEverySend()
    {
        // Direto no container, depois do AddTecCqrs, o authorizer da interface nunca entra nos alvos do pipeline: a
        // requisição rodaria sem ele (fail-open). Sem host, a verificação roda na primeira autorização e continua falhando
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecCqrs(opt => opt
            .AddCommandHandler<ConcreteEntityRequest, ConcreteEntityRequestHandler>()
            .RequireAuthorization = false);
        services.AddScoped<IRequestAuthorizer<IAuthorizedEntityRequest>, BaseAuthorizer>();
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var ex = await Assert.That(async () => await mediator.Send(new ConcreteEntityRequest())).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains(typeof(IAuthorizedEntityRequest).FullName!);
        await Assert.That(ex.Message).Contains("AddRequestAuthorizer");
        await Assert.That(async () => await mediator.Send(new ConcreteEntityRequest())).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task BaseInterfaceAuthorizer_RegisteredDirectlyInContainerAfterAddTecCqrs_FailsAtHostStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecCqrs(opt => opt
                .AddCommandHandler<ConcreteEntityRequest, ConcreteEntityRequestHandler>()
                .RequireAuthorization = false)
            .AddAspNetCore();
        services.AddScoped<IRequestAuthorizer<IAuthorizedEntityRequest>, BaseAuthorizer>();
        await using var sp = services.BuildServiceProvider();

        var ex = await Assert.That(async () =>
        {
            foreach (var hosted in sp.GetServices<IHostedService>())
                await hosted.StartAsync(CancellationToken.None);
        }).ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(IAuthorizedEntityRequest).FullName!);
    }

    [Test]
    public async Task Anonymous_user_is_refused_before_any_authorizer_is_created()
    {
        // Só authorizers: o anônimo não pode fazer o container criar o authorizer (e as dependências dele)
        await using var sp = BuildWithTrackedAuthorizer(principal: null);
        await using var scope = sp.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ConcreteEntityRequest());

        await Assert.That(result.Errors[0].Type).IsEqualTo(ErrorType.Unauthorized);
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain(TrackedAuthorizer.Created);
    }

    [Test]
    public async Task Authenticated_user_gets_the_authorizer_created_and_executed()
    {
        await using var sp = BuildWithTrackedAuthorizer(FakePrincipalAccessor.User("maria"));
        await using var scope = sp.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ConcreteEntityRequest());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo([TrackedAuthorizer.Created, TrackedAuthorizer.Executed]);
    }

    [Test]
    public async Task Request_denied_by_attributes_never_creates_the_authorizers()
    {
        await using var sp = BuildWithTrackedAuthorizer(FakePrincipalAccessor.User("maria"));
        await using var scope = sp.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new AdminEntityRequest());

        await Assert.That(result.Errors[0].Type).IsEqualTo(ErrorType.Forbidden);
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain(TrackedAuthorizer.Created);
    }

    private static ServiceProvider BuildWithTrackedAuthorizer(System.Security.Claims.ClaimsPrincipal? principal)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CallLog>();
        services.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = principal });
        services.AddTecCqrs(opt => opt
            .AddCommandHandler<ConcreteEntityRequest, ConcreteEntityRequestHandler>()
            .AddCommandHandler<AdminEntityRequest, AdminEntityRequestHandler>()
            .AddRequestAuthorizer<IAuthorizedEntityRequest, TrackedAuthorizer>());
        return services.BuildServiceProvider();
    }

    public interface IAuthorizedEntityRequest : IBaseRequest;

    [SkipValidation]
    public record ConcreteEntityRequest : ICommand, IAuthorizedEntityRequest;

    public class ConcreteEntityRequestHandler : ICommandHandler<ConcreteEntityRequest>
    {
        public Task<Result> Handle(ConcreteEntityRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    public class BaseAuthorizer : IRequestAuthorizer<IAuthorizedEntityRequest>
    {
        public Task<Result> AuthorizeAsync(IAuthorizedEntityRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure(Error.Forbidden("AUTH_DENIED", "Access denied by base authorizer")));
    }

    [AuthorizeRequest(Roles = "Admin")]
    [SkipValidation]
    public record AdminEntityRequest : ICommand, IAuthorizedEntityRequest;

    public class AdminEntityRequestHandler : ICommandHandler<AdminEntityRequest>
    {
        public Task<Result> Handle(AdminEntityRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    /// <summary>Authorizer que registra quando é criado e executado (autoriza sempre).</summary>
    public class TrackedAuthorizer : IRequestAuthorizer<IAuthorizedEntityRequest>
    {
        public const string Created = "authorizer-criado";
        public const string Executed = "authorizer-executado";

        private readonly CallLog _log;

        public TrackedAuthorizer(CallLog log)
        {
            _log = log;
            log.Add(Created);
        }

        public Task<Result> AuthorizeAsync(IAuthorizedEntityRequest request, CancellationToken cancellationToken)
        {
            _log.Add(Executed);
            return Task.FromResult(Result.Success());
        }
    }

    // Issue #7: Recursão além de MaxRecursionDepth lança InvalidOperationException
    [Test]
    public async Task MaxRecursionDepth_ThrowsInvalidOperationException_WhenDepthExceeded()
    {
        var pipeline = new PipelineContext();
        var frames = new List<RequestFrame>();

        for (int i = 0; i < PipelineContext.MaxRecursionDepth; i++)
        {
            frames.Add(pipeline.Enter(isCommand: true));
        }

        // A próxima chamada ultrapassa o limite
        await Assert.That(() => pipeline.Enter(isCommand: true)).Throws<InvalidOperationException>();

        // Cleanup
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            pipeline.Exit(frames[i]);
        }
    }
}
