using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

/// <summary>
/// Telemetria no formato coletado pelo TEC.Observability: fontes com prefixo <c>TEC.</c>, <c>cqrs.outcome</c> e <c>error.type</c>
/// nas <c>Activity</c>s (os mesmos atributos das métricas) e trace e métricas das notificações.
/// </summary>
public class ObservabilityTests
{
    [Test]
    public async Task Sources_use_the_prefix_exported_by_TEC_Observability()
    {
        // O AddTecObservability registra "TEC.*" como ActivitySource e Meter: renomear quebraria a exportação em silêncio
        await Assert.That(CqrsDiagnostics.ActivitySourceName).StartsWith("TEC.");
        await Assert.That(CqrsDiagnostics.MeterName).StartsWith("TEC.");
    }

    [Test]
    public async Task Request_activity_has_outcome_and_error_type_like_the_metrics()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build();

        using (var scope = provider.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new CreateCustomerCommand("Maria", "12345678909"));
            await sender.Send(new DeactivateCustomerCommand(Guid.Empty));
            await Assert.That(async () => { await sender.Send(new FailCommand()); }).ThrowsExactly<InvalidOperationException>();
        }

        var successActivity = capture.Single(nameof(CreateCustomerCommand));
        var failureActivity = capture.Single(nameof(DeactivateCustomerCommand));
        var exceptionActivity = capture.Single(nameof(FailCommand));

        await Assert.That(successActivity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("success");
        await Assert.That(successActivity.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsNull();
        await Assert.That(failureActivity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("failure");
        await Assert.That(failureActivity.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsEqualTo(nameof(ErrorType.BusinessRule));
        await Assert.That(failureActivity.Status).IsNotEqualTo(ActivityStatusCode.Error);
        await Assert.That(exceptionActivity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("exception");
        await Assert.That(exceptionActivity.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(exceptionActivity.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Publish_produces_notification_activity_and_metrics()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build(services: s => s.AddMetrics());
        using var metrics = new MetricCapture(provider.GetRequiredService<IMeterFactory>());

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new CustomerCreatedEvent(Guid.NewGuid()));

        var activity = capture.Single(nameof(CustomerCreatedEvent));
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.NotificationTag)).IsEqualTo(typeof(CustomerCreatedEvent).FullName);
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.KindTag)).IsEqualTo("notification");
        await Assert.That(activity.GetTagItem("cqrs.after_commit") is false).IsTrue();
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("success");

        var counter = metrics.Of(CqrsDiagnostics.NotificationsMetricName);
        await Assert.That(counter.Length).IsEqualTo(1);
        await Assert.That(counter[0][CqrsDiagnostics.NotificationTag]).IsEqualTo(typeof(CustomerCreatedEvent).FullName);
        await Assert.That(counter[0][CqrsDiagnostics.OutcomeTag]).IsEqualTo("success");
        await Assert.That(metrics.Of(CqrsDiagnostics.NotificationDurationMetricName).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Failing_post_commit_handler_marks_publication_as_exception()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build(withUnitOfWork: true, services: s => s.AddMetrics());
        using var metrics = new MetricCapture(provider.GetRequiredService<IMeterFactory>());

        using (var scope = provider.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishFailingEventCommand());
            await Assert.That(result.IsSuccess).IsTrue();
        }

        var activity = capture.Single(nameof(FailingEvent));
        await Assert.That(activity.GetTagItem("cqrs.after_commit") is true).IsTrue();
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("exception");
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.Events.Any(e => e.Name == "exception")).IsTrue();

        var counter = metrics.Of(CqrsDiagnostics.NotificationsMetricName);
        await Assert.That(counter.Single()[CqrsDiagnostics.OutcomeTag]).IsEqualTo("exception");
    }

    [Test]
    public async Task Immediate_Publish_with_failing_handler_logs_and_rethrows_the_exception()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build();

        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new FailingEvent()); })
                .ThrowsExactly<InvalidOperationException>();
        }

        var activity = capture.Single(nameof(FailingEvent));
        await Assert.That(activity.GetTagItem("cqrs.after_commit") is false).IsTrue();
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("exception");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Exception_event_carries_only_exception_type_by_default()
    {
        // Mensagem e stack trace (drivers, HTTP) podem conter dados pessoais ou tokens: ficam só no log
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build();

        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FailCommand()); })
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new FailingEvent()); })
                .ThrowsExactly<InvalidOperationException>();
        }

        foreach (var activity in new[] { capture.Single(nameof(FailCommand)), capture.Single(nameof(FailingEvent)) })
        {
            var tags = activity.Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value);

            await Assert.That(tags.Keys.ToArray()).IsEquivalentTo(new[] { "exception.type" }, CollectionOrdering.Matching);
            await Assert.That(tags["exception.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
            await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
            await Assert.That(activity.StatusDescription).IsEqualTo(nameof(InvalidOperationException));
        }
    }

    [Test]
    public async Task Exception_event_carries_message_and_stack_trace_with_RecordExceptionDetailsInTraces()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build(o => o.RecordExceptionDetailsInTraces = true);

        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FailCommand()); })
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new FailingEvent()); })
                .ThrowsExactly<InvalidOperationException>();
        }

        var requestTags = capture.Single(nameof(FailCommand)).Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value);
        var notificationTags = capture.Single(nameof(FailingEvent)).Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value);

        await Assert.That(requestTags["exception.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(requestTags["exception.message"]).IsEqualTo("detalhe interno: connection string xyz");
        await Assert.That(requestTags.ContainsKey("exception.stacktrace")).IsTrue();
        await Assert.That(notificationTags["exception.message"]).IsEqualTo("SMTP fora do ar");
        await Assert.That(notificationTags.ContainsKey("exception.stacktrace")).IsTrue();
    }

    [Test]
    public async Task RecordExceptionDetailsInTraces_is_disabled_by_default() =>
        await Assert.That(new TEC.Cqrs.DependencyInjection.CqrsOptions().RecordExceptionDetailsInTraces).IsFalse();

    /// <summary>
    /// <c>Activity</c>s do TEC.Cqrs geradas pelo próprio teste: abre um trace novo e só guarda o que pertence a ele (os testes
    /// rodam em paralelo e usam as mesmas requisições).
    /// </summary>
    private sealed class ActivityCapture : IDisposable
    {
        private static readonly ActivitySource TestSource = new("TEC.Cqrs.Tests.Observability");

        private readonly ActivityListener _listener;
        private readonly Activity _root;
        private readonly List<Activity> _stopped = [];

        public ActivityCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CqrsDiagnostics.ActivitySourceName || source == TestSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_stopped)
                        _stopped.Add(activity);
                }
            };
            ActivitySource.AddActivityListener(_listener);

            // O TUnit executa cada teste dentro de uma Activity: sem limpar, o trace seria o dele
            Activity.Current = null;
            _root = TestSource.StartActivity("teste")!;
        }

        public Activity Single(string displayName)
        {
            lock (_stopped)
                return _stopped.Single(a => a.TraceId == _root.TraceId && a.DisplayName == displayName);
        }

        public void Dispose()
        {
            _root.Dispose();
            _listener.Dispose();
        }
    }

    /// <summary>Medições do Meter do TEC.Cqrs criado pelo <see cref="IMeterFactory"/> do container do teste.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<(string Instrument, Dictionary<string, object?> Tags)> _measurements = [];

        public MetricCapture(IMeterFactory factory)
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == CqrsDiagnostics.MeterName && instrument.Meter.Scope == factory)
                        l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
            _listener.Start();
        }

        public Dictionary<string, object?>[] Of(string instrument)
        {
            lock (_measurements)
                return [.. _measurements.Where(m => m.Instrument == instrument).Select(m => m.Tags)];
        }

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;
            lock (_measurements)
                _measurements.Add((instrument.Name, copy));
        }

        public void Dispose() => _listener.Dispose();
    }
}
