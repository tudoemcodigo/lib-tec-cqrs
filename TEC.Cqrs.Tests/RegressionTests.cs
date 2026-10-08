using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class ValidationRegressionTests
{
    [Test]
    public async Task Two_validators_do_not_duplicate_errors()
    {
        // Risco: um ValidationContext compartilhado acumularia as falhas e o segundo validator devolveria também as do primeiro
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new TwoValidatorsCommand("", ""));

        await Assert.That(result.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "CPF_OBRIGATORIO", "NOME_OBRIGATORIO" }, CollectionOrdering.Any);
        await Assert.That(result.Errors.Count).IsEqualTo(2);
    }
}

public class PolymorphicNotificationTests
{
    [Test]
    public async Task Interface_handlers_receive_the_concrete_event_only_once()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new CustomerUpdatedEvent(Guid.NewGuid()));

        // Tipo concreto primeiro (por nome do handler), depois a interface; o HandlerDuplo roda só pelo tipo concreto
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "atualizado", "duplo:concreto", "auditoria" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Interface_handlers_receive_the_concrete_event_after_commit()
    {
        using var provider = TestHost.Build(withUnitOfWork: true, services: s =>
            s.AddScoped<INotificationHandler<EventWithBrokenHandler>, NoOpHandler>());
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishBrokenEventCommand());

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "commit", "auditoria", "duplo:interface" }, CollectionOrdering.Matching);
    }

    private sealed class NoOpHandler : INotificationHandler<EventWithBrokenHandler>
    {
        public Task Handle(EventWithBrokenHandler notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class OptionsRegressionTests
{
    [Test]
    public async Task Options_cannot_be_changed_after_AddTecCqrs()
    {
        using var provider = TestHost.Build();
        var options = provider.GetRequiredService<CqrsOptions>();

        await Assert.That(() => options.RequireAuthorization = false).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.RequireValidatorForCommands = false).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.SlowRequestThreshold = null).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.RecordExceptionDetailsInTraces = true).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.RegisterServicesFromAssemblyContaining<CallLog>()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.AddBehavior(typeof(RecordingBehavior<,>))).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.AddCommandHandler<DeactivateCustomerCommand, DeactivateCustomerHandler>()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(options.RequireAuthorization).IsTrue();

        var validation = provider.GetRequiredService<CqrsFluentValidationOptions>();
        await Assert.That(() => validation.CamelCaseValidationFields = false).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => validation.AddValidator<CreateCustomerCommand, CreateCustomerValidator>()).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Options_can_be_changed_before_AddTecCqrs()
    {
        var options = new CqrsOptions { RequireAuthorization = false, SlowRequestThreshold = null };

        await Assert.That(options.RequireAuthorization).IsFalse();
        await Assert.That(options.SlowRequestThreshold).IsNull();
    }
}

public class LoggingRegressionTests
{
    private static (ServiceProvider Provider, ListLoggerProvider Logs) Build(Action<CqrsOptions>? configure = null)
    {
        var logs = new ListLoggerProvider();
        var provider = TestHost.Build(configure, services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
        });
        return (provider, logs);
    }

    private static IEnumerable<ListLoggerProvider.LogEntry> Pipeline(ListLoggerProvider logs) =>
        logs.Entries.Where(e => e.Category == typeof(Mediator).FullName);

    [Test]
    public async Task Internal_AppException_produces_a_single_error_log_with_the_exception()
    {
        // Risco: ExceptionBehavior e LoggingBehavior registrarem a mesma falha (dois logs de erro)
        var (provider, logs) = Build();
        using (provider)
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new IntegrateCommand());

        var errors = Pipeline(logs).Where(e => e.Level >= LogLevel.Error).ToArray();
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Exception).IsTypeOf<TEC.Core.Exceptions.IntegrationException>();
    }

    [Test]
    public async Task Nested_command_exception_is_logged_once_and_not_again_in_exception_handler()
    {
        var (provider, logs) = Build();
        Exception? thrown = null;
        using (provider)
        using (var scope = provider.CreateScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ParentOfFailCommand());
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
        }

        await Assert.That(Pipeline(logs).Count(e => e.EventId.Id == 1004)).IsEqualTo(1);
        await Assert.That(ApplicationBuilderExtensions.SuppressDiagnostics(thrown)).IsTrue();
        await Assert.That(ApplicationBuilderExtensions.SuppressDiagnostics(new InvalidOperationException("não registrada"))).IsFalse();
    }

    [Test]
    public async Task Exception_is_not_marked_as_logged_when_error_log_is_disabled()
    {
        // Risco: marcar a exceção sem o log ter sido escrito faria o UseTecExceptionHandler não registrá-la:
        // a falha ficaria sem nenhum registro
        var logs = new ListLoggerProvider();
        var provider = TestHost.Build(services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.AddLogging(b => b.AddFilter(typeof(Mediator).FullName, LogLevel.None));
        });
        Exception? thrown = null;
        using (provider)
        using (var scope = provider.CreateScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ParentOfFailCommand());
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(Pipeline(logs)).IsEmpty();
        await Assert.That(CqrsDiagnostics.IsExceptionLogged(thrown)).IsFalse();
        await Assert.That(ApplicationBuilderExtensions.SuppressDiagnostics(thrown)).IsFalse();
    }

    [Test]
    public async Task Cancellation_logs_at_low_level_and_marks_the_activity()
    {
        List<Activity> stopped = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == nameof(CancellableQuery))
                {
                    lock (stopped)
                        stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var (provider, logs) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using (provider)
        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancellableQuery(), cts.Token); })
                .Throws<OperationCanceledException>();
        }

        var entries = Pipeline(logs).ToArray();
        await Assert.That(entries.Any(e => e.EventId.Id == 1005 && e.Level == LogLevel.Debug)).IsTrue();
        await Assert.That(entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse();

        Activity activity;
        lock (stopped)
            activity = stopped.Single();
        await Assert.That(activity.GetTagItem("cqrs.canceled") is true).IsTrue();
        await Assert.That(activity.GetTagItem("cqrs.success") is false).IsTrue();
        await Assert.That(activity.Status).IsNotEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Performance_also_measures_executions_ending_with_exception()
    {
        var (provider, logs) = Build(o => o.SlowRequestThreshold = TimeSpan.FromMilliseconds(1));
        using (provider)
        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SlowWithExceptionCommand()); })
                .ThrowsExactly<TimeoutException>();
        }

        await Assert.That(Pipeline(logs).Count(e => e.EventId.Id == 1006)).IsEqualTo(1);
    }
}

public class TypeScannerTests
{
    [Test]
    public async Task Types_that_fail_to_load_produce_clear_error_instead_of_being_ignored()
    {
        var assembly = new AssemblyWithBrokenTypes();

        var ex = await Assert.That(() => TypeScanner.GetConcreteTypes(assembly).ToArray()).ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Dependencia.Ausente");
        await Assert.That(ex.InnerException).IsTypeOf<ReflectionTypeLoadException>();
    }

    [Test]
    public async Task Types_are_ordered_by_full_name()
    {
        var names = TypeScanner.GetConcreteTypes(typeof(CallLog).Assembly).Select(t => t.FullName).ToArray();

        await Assert.That(names).IsEquivalentTo(names.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    private sealed class AssemblyWithBrokenTypes : Assembly
    {
        public override string FullName => "Quebrado, Version=1.0.0.0";

        public override Type[] GetTypes() => throw new ReflectionTypeLoadException(
            [typeof(CallLog), null],
            [new FileNotFoundException("Could not load file or assembly 'Dependencia.Ausente'.")]);
    }
}
