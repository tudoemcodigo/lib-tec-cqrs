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
    public async Task Fontes_usam_o_prefixo_exportado_pelo_TEC_Observability()
    {
        // O AddEnterpriseObservability registra "TEC.*" como ActivitySource e Meter: renomear quebraria a exportação em silêncio
        await Assert.That(CqrsDiagnostics.ActivitySourceName).StartsWith("TEC.");
        await Assert.That(CqrsDiagnostics.MeterName).StartsWith("TEC.");
    }

    [Test]
    public async Task Activity_da_requisicao_tem_outcome_e_error_type_como_as_metricas()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build();

        using (var scope = provider.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new CriarClienteCommand("Maria", "12345678909"));
            await sender.Send(new InativarClienteCommand(Guid.Empty));
            await Assert.That(async () => { await sender.Send(new FalharCommand()); }).ThrowsExactly<InvalidOperationException>();
        }

        var sucesso = capture.Single(nameof(CriarClienteCommand));
        var falha = capture.Single(nameof(InativarClienteCommand));
        var excecao = capture.Single(nameof(FalharCommand));

        await Assert.That(sucesso.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("success");
        await Assert.That(sucesso.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsNull();
        await Assert.That(falha.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("failure");
        await Assert.That(falha.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsEqualTo(nameof(ErrorType.BusinessRule));
        await Assert.That(falha.Status).IsNotEqualTo(ActivityStatusCode.Error);
        await Assert.That(excecao.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("exception");
        await Assert.That(excecao.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(excecao.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Publish_gera_activity_e_metricas_da_notificacao()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build(services: s => s.AddMetrics());
        using var metrics = new MetricCapture(provider.GetRequiredService<IMeterFactory>());

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new ClienteCriadoEvent(Guid.NewGuid()));

        var activity = capture.Single(nameof(ClienteCriadoEvent));
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.NotificationTag)).IsEqualTo(typeof(ClienteCriadoEvent).FullName);
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.KindTag)).IsEqualTo("notification");
        await Assert.That(activity.GetTagItem("cqrs.after_commit") is false).IsTrue();
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("success");

        var counter = metrics.Of(CqrsDiagnostics.NotificationsMetricName);
        await Assert.That(counter.Length).IsEqualTo(1);
        await Assert.That(counter[0][CqrsDiagnostics.NotificationTag]).IsEqualTo(typeof(ClienteCriadoEvent).FullName);
        await Assert.That(counter[0][CqrsDiagnostics.OutcomeTag]).IsEqualTo("success");
        await Assert.That(metrics.Of(CqrsDiagnostics.NotificationDurationMetricName).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Handler_pos_commit_que_falha_marca_a_publicacao_como_exception()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build(withUnitOfWork: true, services: s => s.AddMetrics());
        using var metrics = new MetricCapture(provider.GetRequiredService<IMeterFactory>());

        using (var scope = provider.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublicarEventoComFalhaCommand());
            await Assert.That(result.IsSuccess).IsTrue();
        }

        var activity = capture.Single(nameof(EventoComFalha));
        await Assert.That(activity.GetTagItem("cqrs.after_commit") is true).IsTrue();
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("exception");
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.ErrorTypeTag)).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.Events.Any(e => e.Name == "exception")).IsTrue();

        var counter = metrics.Of(CqrsDiagnostics.NotificationsMetricName);
        await Assert.That(counter.Single()[CqrsDiagnostics.OutcomeTag]).IsEqualTo("exception");
    }

    [Test]
    public async Task Publish_imediato_com_handler_que_falha_registra_e_repassa_a_excecao()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build();

        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new EventoComFalha()); })
                .ThrowsExactly<InvalidOperationException>();
        }

        var activity = capture.Single(nameof(EventoComFalha));
        await Assert.That(activity.GetTagItem("cqrs.after_commit") is false).IsTrue();
        await Assert.That(activity.GetTagItem(CqrsDiagnostics.OutcomeTag)).IsEqualTo("exception");
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Evento_exception_leva_so_o_tipo_da_excecao_por_padrao()
    {
        // Mensagem e stack trace (drivers, HTTP) podem conter dados pessoais ou tokens: ficam só no log
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build();

        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FalharCommand()); })
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new EventoComFalha()); })
                .ThrowsExactly<InvalidOperationException>();
        }

        foreach (var activity in new[] { capture.Single(nameof(FalharCommand)), capture.Single(nameof(EventoComFalha)) })
        {
            var tags = activity.Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value);

            await Assert.That(tags.Keys.ToArray()).IsEquivalentTo(new[] { "exception.type" }, CollectionOrdering.Matching);
            await Assert.That(tags["exception.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
            await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
            await Assert.That(activity.StatusDescription).IsEqualTo(nameof(InvalidOperationException));
        }
    }

    [Test]
    public async Task Evento_exception_leva_mensagem_e_stack_trace_com_RecordExceptionDetailsInTraces()
    {
        using var capture = new ActivityCapture();
        using var provider = TestHost.Build(o => o.RecordExceptionDetailsInTraces = true);

        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FalharCommand()); })
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new EventoComFalha()); })
                .ThrowsExactly<InvalidOperationException>();
        }

        var requisicao = capture.Single(nameof(FalharCommand)).Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value);
        var notificacao = capture.Single(nameof(EventoComFalha)).Events.Single(e => e.Name == "exception").Tags.ToDictionary(t => t.Key, t => t.Value);

        await Assert.That(requisicao["exception.type"]).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(requisicao["exception.message"]).IsEqualTo("detalhe interno: connection string xyz");
        await Assert.That(requisicao.ContainsKey("exception.stacktrace")).IsTrue();
        await Assert.That(notificacao["exception.message"]).IsEqualTo("SMTP fora do ar");
        await Assert.That(notificacao.ContainsKey("exception.stacktrace")).IsTrue();
    }

    [Test]
    public async Task RecordExceptionDetailsInTraces_e_desativado_por_padrao() =>
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
