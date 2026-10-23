using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class NestedConcurrencyTests
{
    private static async Task<(Result? Result, Exception? Exception, IReadOnlyList<string> Calls)> RunAsync(ChildrenMode mode)
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ParentOfChildrenCommand(mode));
            return (result, null, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
        }
    }

    [Test]
    public async Task Parallel_inner_commands_are_rejected_and_transaction_is_rolled_back()
    {
        // Risco: os dois commands internos usariam a mesma transação (e o mesmo DbContext) ao mesmo tempo
        var (_, exception, calls) = await RunAsync(ChildrenMode.Parallel);

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("em paralelo a outro command interno na mesma transação");
        await Assert.That(exception.Message).Contains(typeof(ChildCommand).FullName!);
        await Assert.That(calls).DoesNotContain("filho:B");
        await Assert.That(calls).DoesNotContain("commit");
        await Assert.That(calls[^1]).IsEqualTo("rollback");
    }

    [Test]
    public async Task Rejection_handled_by_outer_handler_still_rolls_back_transaction()
    {
        var (result, _, calls) = await RunAsync(ChildrenMode.ParallelHandled);

        await Assert.That(result!.Error!.Code).IsEqualTo("TRANSACAO_DESFEITA");
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "filho:A", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Sequential_inner_commands_are_still_allowed()
    {
        var (result, _, calls) = await RunAsync(ChildrenMode.Sequential);

        await Assert.That(result!.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "filho:A", "filho:B", "pai", "commit" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Command_inside_inner_command_is_still_allowed()
    {
        var (result, _, calls) = await RunAsync(ChildrenMode.SequentialWithGrandchild);

        await Assert.That(result!.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(
            new[] { "begin", "filho:A", "filho:A.neto", "filho:B", "filho:B.neto", "pai", "commit" }, CollectionOrdering.Matching);
    }
}

public class DeferredNotificationLimitTests
{
    [Test]
    public async Task PublishAfterCommit_cycle_stops_at_round_limit_with_log()
    {
        // Risco: um handler pós-commit que publica de novo laçaria para sempre
        var logs = new ListLoggerProvider();
        using var provider = TestHost.Build(withUnitOfWork: true, services: s => s.AddSingleton<ILoggerProvider>(logs));
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishCycleCommand());

        const int max = RequestHandlerWrapperImpl<PublishCycleCommand, Result>.MaxDeferredRounds;
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries.Count(e => e.StartsWith("ciclo:", StringComparison.Ordinal)))
            .IsEqualTo(max);
        var entry = logs.Entries.Single(e => e.EventId.Id == 1016);
        await Assert.That(entry.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(entry.Message).Contains(nameof(CycleEvent));
    }

    [Test]
    public async Task PublishAfterCommit_with_transaction_opened_outside_pipeline_discards_notifications_and_throws()
    {
        // Risco: publicar antes do commit externo anunciaria dados que ainda podem ser desfeitos (e-mail de algo não gravado)
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        var ex = await Assert.That(async () =>
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateWithEventCommand());
            })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("transação aberta fora do pipeline");
        await Assert.That(ex.Message).Contains(nameof(CustomerCreatedEvent));
        await Assert.That(unitOfWork.HasActiveTransaction).IsTrue(); // quem abriu decide o rollback
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "handler" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Failure_with_transaction_opened_outside_pipeline_discards_notifications_without_rollback()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateWithEventCommand(Fail: true));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(unitOfWork.HasActiveTransaction).IsTrue(); // quem abriu decide o rollback
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "handler" }, CollectionOrdering.Matching);
    }
}

public class NullResponseTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Query_handler_returning_null_throws_clear_exception(bool isAsync)
    {
        // Risco: NullReferenceException no LoggingBehavior, sem indicar o handler
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () =>
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ReturnsNullQuery(isAsync));
            })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("retornou null");
        await Assert.That(ex.Message).Contains(typeof(ReturnsNullQuery).FullName!);
        await Assert.That(ex.Message).Contains(nameof(ReturnsNullQueryHandler));
    }

    [Test]
    public async Task Behavior_returning_null_throws_clear_exception()
    {
        using var provider = TestHost.Build(o => o.AddBehavior(typeof(ReturnsNullBehavior<,>)));
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishesQuery()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("um behavior retornou null");
    }
}

public class BehaviorRegistrationOrderTests
{
    [Test]
    public async Task Open_behavior_registered_before_AddTecCqrs_fails_at_startup()
    {
        // Risco: o behavior ficaria mais externo que a autorização e a validação, sem aviso
        var services = new ServiceCollection();
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(RecordingBehavior<,>));

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("antes do AddTecCqrs");
        await Assert.That(ex.Message).Contains("AddBehavior");
        await Assert.That(ex.Message).Contains("RecordingBehavior");
    }

    [Test]
    public async Task Closed_behavior_registered_before_AddTecCqrs_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPipelineBehavior<PublishesQuery, Result<int>>, RecordingBehavior<PublishesQuery, Result<int>>>();

        await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>()))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Behavior_registered_after_AddTecCqrs_runs_inside_the_transaction()
    {
        using var provider = TestHost.Build(withUnitOfWork: true, services: s =>
            s.AddScoped(typeof(IPipelineBehavior<,>), typeof(RecordingBehavior<,>)));
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("Maria", "12345678909"));

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).IsEquivalentTo(
            new[] { "begin", "behavior:antes", "handler", "behavior:depois", "commit" }, CollectionOrdering.Matching);
    }
}

public class AuthorizerResolutionTests
{
    [Test]
    public async Task Exact_type_authorizer_registered_after_AddTecCqrs_is_still_executed()
    {
        // A resolução só dos authorizers conhecidos pelo container não pode ignorar registros feitos depois (fail-open)
        using var provider = TestHost.Build(services: s => s.AddScoped<IRequestAuthorizer<PublishesQuery>, DenyAuthorizer<PublishesQuery>>());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishesQuery());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE");
    }

    [Test]
    public async Task Open_generic_authorizer_registered_directly_in_container_is_still_executed()
    {
        using var provider = TestHost.Build(services: s => s.AddScoped(typeof(IRequestAuthorizer<>), typeof(DenyAuthorizer<>)));
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishesQuery());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE");
    }
}

public class ExceptionMapperLoggingTests
{
    [Test]
    public async Task Exception_converted_by_mapper_is_attached_to_failure_log_and_not_to_response()
    {
        var logs = new ListLoggerProvider();
        using var provider = TestHost.Build(services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionErrorMapper, TimeoutErrorMapper>());
        });
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SlowWithExceptionCommand());

        await Assert.That(result.Error!.Code).IsEqualTo("TIMEOUT_MAPEADO");
        await Assert.That(result.Errors.Any(e => e.Message.Contains("timeout no banco", StringComparison.Ordinal))).IsFalse();
        var failure = logs.Entries.Single(e => e.EventId.Id == 1003);
        await Assert.That(failure.Exception).IsTypeOf<TimeoutException>();
        await Assert.That(logs.Entries.Count(e => e.Level >= LogLevel.Error)).IsEqualTo(1);
    }
}

public class AsyncValidationTests
{
    private static async Task<(Result Result, IReadOnlyList<string> Calls)> RunAsync(string nickname)
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ReserveNicknameCommand(nickname));
        return (result, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
    }

    [Test]
    public async Task MustAsync_is_executed_by_the_pipeline()
    {
        var (result, calls) = await RunAsync("admin");

        await Assert.That(result.Errors.Select(e => e.Code)).IsEquivalentTo(new[] { "APELIDO_RESERVADO" }, CollectionOrdering.Matching);
        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Validation);
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task CustomAsync_is_executed_by_the_pipeline()
    {
        var (result, calls) = await RunAsync("apelido-muito-longo");

        await Assert.That(result.Errors.Select(e => e.Code)).IsEquivalentTo(new[] { "APELIDO_LONGO" }, CollectionOrdering.Matching);
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Valid_async_rules_reach_the_handler()
    {
        var (result, calls) = await RunAsync("maria");

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(new[] { "handler" }, CollectionOrdering.Matching);
    }
}

public class PrincipalAccessorRegistrationTests
{
    private static ICqrsBuilder AddCqrs(IServiceCollection services) =>
        services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>());

    [Test]
    public async Task Accessor_registered_before_AddAspNetCore_fails_at_startup()
    {
        // Risco: manter o accessor do job em silêncio faria a autorização HTTP ignorar o HttpContext.User
        var services = new ServiceCollection();
        services.AddScoped<IPrincipalAccessor, JobUserAccessor>();
        var builder = AddCqrs(services);

        var ex = await Assert.That(() => builder.AddAspNetCore()).ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(JobUserAccessor));
        await Assert.That(ex.Message).Contains(nameof(CqrsAspNetCoreOptions.ReplaceExistingPrincipalAccessor));
    }

    [Test]
    public async Task ReplaceExistingPrincipalAccessor_replaces_with_HttpContext()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPrincipalAccessor, JobUserAccessor>();
        AddCqrs(services).AddAspNetCore(http => http.ReplaceExistingPrincipalAccessor = true);

        await Assert.That(services.Count(d => d.ServiceType == typeof(IPrincipalAccessor))).IsEqualTo(1);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Assert.That(scope.ServiceProvider.GetRequiredService<IPrincipalAccessor>()).IsTypeOf<HttpContextPrincipalAccessor>();
    }

    [Test]
    public async Task Accessor_registered_afterwards_with_Replace_is_kept()
    {
        var services = new ServiceCollection();
        AddCqrs(services).AddAspNetCore();
        services.Replace(ServiceDescriptor.Scoped<IPrincipalAccessor, JobUserAccessor>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Assert.That(scope.ServiceProvider.GetRequiredService<IPrincipalAccessor>()).IsTypeOf<JobUserAccessor>();
    }

    [Test]
    public async Task Without_previous_accessor_registers_the_HttpContext_one()
    {
        var services = new ServiceCollection();
        AddCqrs(services).AddAspNetCore();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await Assert.That(scope.ServiceProvider.GetRequiredService<IPrincipalAccessor>()).IsTypeOf<HttpContextPrincipalAccessor>();
    }
}
