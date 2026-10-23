using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using TEC.Cqrs.Diagnostics;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Métricas do pipeline (Meter <see cref="CqrsDiagnostics.MeterName"/>), seguindo as convenções do OpenTelemetry:
/// nomes em minúsculas separados por ponto, duração em segundos (histograma) e atributos de baixa cardinalidade.
/// </summary>
/// <remarks>
/// Usa o <see cref="IMeterFactory"/> do container quando registrado (<c>services.AddMetrics()</c>, padrão no ASP.NET Core),
/// o que isola as métricas por container (útil em testes); senão, um <see cref="Meter"/> compartilhado.
/// </remarks>
internal sealed class CqrsMetrics
{
    private static readonly string? Version =
        typeof(CqrsMetrics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly Lazy<Meter> SharedMeter = new(() => new Meter(CqrsDiagnostics.MeterName, Version));

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _notifications;
    private readonly Histogram<double> _notificationDuration;

    public CqrsMetrics(IMeterFactory? meterFactory = null)
    {
        var meter = meterFactory?.Create(new MeterOptions(CqrsDiagnostics.MeterName) { Version = Version }) ?? SharedMeter.Value;

        _requests = meter.CreateCounter<long>(
            CqrsDiagnostics.RequestsMetricName,
            unit: "{request}",
            description: "Requisições (commands/queries) processadas pelo pipeline, por requisição e resultado.");
        _duration = CreateDurationHistogram(meter, CqrsDiagnostics.RequestDurationMetricName,
            "Duração das requisições (commands/queries) no pipeline, do primeiro behavior ao retorno do handler.");

        _notifications = meter.CreateCounter<long>(
            CqrsDiagnostics.NotificationsMetricName,
            unit: "{notification}",
            description: "Publicações de notificação (todos os handlers), por notificação e resultado.");
        _notificationDuration = CreateDurationHistogram(meter, CqrsDiagnostics.NotificationDurationMetricName,
            "Duração das publicações de notificação, somando todos os handlers.");
    }

    private static Histogram<double> CreateDurationHistogram(Meter meter, string name, string description)
    {
#if NET9_0_OR_GREATER
        return meter.CreateHistogram(name, unit: "s", description: description,
            advice: new InstrumentAdvice<double>
            {
                // Mesmos limites recomendados pelo OpenTelemetry para http.server.request.duration
                HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
            });
#else
        return meter.CreateHistogram<double>(name, unit: "s", description: description);
#endif
    }

    /// <summary>Indica se alguém está coletando as métricas de requisição (evita montar as tags à toa).</summary>
    public bool Enabled => _requests.Enabled || _duration.Enabled;

    /// <summary>Indica se alguém está coletando as métricas de notificação.</summary>
    public bool NotificationsEnabled => _notifications.Enabled || _notificationDuration.Enabled;

    /// <summary>Registra uma publicação de notificação concluída.</summary>
    /// <param name="notification">Nome completo do tipo da notificação.</param>
    /// <param name="outcome"><c>success</c>, <c>exception</c> ou <c>canceled</c>.</param>
    /// <param name="errorType">Tipo da exceção, ou <c>null</c>.</param>
    /// <param name="elapsed">Duração.</param>
    public void RecordNotification(string notification, string outcome, string? errorType, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { CqrsDiagnostics.NotificationTag, notification },
            { CqrsDiagnostics.OutcomeTag, outcome }
        };
        if (errorType is not null)
            tags.Add(CqrsDiagnostics.ErrorTypeTag, errorType);

        _notifications.Add(1, tags);
        _notificationDuration.Record(elapsed.TotalSeconds, tags);
    }

    /// <summary>Registra uma requisição concluída.</summary>
    /// <param name="request">Nome completo do tipo da requisição.</param>
    /// <param name="kind"><c>command</c>, <c>query</c> ou <c>request</c>.</param>
    /// <param name="outcome"><c>success</c>, <c>failure</c>, <c>exception</c> ou <c>canceled</c>.</param>
    /// <param name="errorType">Tipo do erro (<c>ErrorType</c> ou tipo da exceção), ou <c>null</c> em sucesso e cancelamento.</param>
    /// <param name="elapsed">Duração.</param>
    public void Record(string request, string kind, string outcome, string? errorType, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            { CqrsDiagnostics.RequestTag, request },
            { CqrsDiagnostics.KindTag, kind },
            { CqrsDiagnostics.OutcomeTag, outcome }
        };
        if (errorType is not null)
            tags.Add(CqrsDiagnostics.ErrorTypeTag, errorType);

        _requests.Add(1, tags);
        _duration.Record(elapsed.TotalSeconds, tags);
    }
}
