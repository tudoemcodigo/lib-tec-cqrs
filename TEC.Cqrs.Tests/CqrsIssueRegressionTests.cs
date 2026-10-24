using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Behaviors;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Persistence;
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

    // Issue #3: Authorizer registrado em interface base é executado
    [Test]
    public async Task BaseInterfaceAuthorizer_RegisteredInContainer_IsExecuted()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecCqrs(opt =>
        {
            opt.RegisterServicesFromAssemblyContaining<CqrsIssueRegressionTests>();
            opt.AuthorizersRequireAuthentication = false;
        });

        services.AddScoped<IRequestAuthorizer<IAuthorizedEntityRequest>, BaseAuthorizer>();

        var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        var result = await mediator.Send(new ConcreteEntityRequest());
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Errors[0].Code).IsEqualTo("AUTH_DENIED");
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
