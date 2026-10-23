using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Diagnostics;

namespace TEC.Cqrs.Internal;

/// <summary>
/// <see cref="Activity"/> e métricas de uma publicação de notificação (todos os handlers), com os mesmos atributos das
/// requisições. O conteúdo da notificação nunca é registrado; da exceção de um handler, só o tipo (mensagem e stack trace
/// apenas com <c>CqrsOptions.RecordExceptionDetailsInTraces</c>).
/// </summary>
internal sealed class NotificationTelemetry : IDisposable
{
    private const string Success = "success";
    private const string ExceptionOutcome = "exception";
    private const string Canceled = "canceled";

    private readonly Activity? _activity;
    private readonly CqrsMetrics? _metrics;
    private readonly string _notification;
    private readonly bool _exceptionDetails;
    private readonly long _start = Stopwatch.GetTimestamp();
    private string? _firstErrorType;

    private NotificationTelemetry(Type notificationType, CqrsMetrics? metrics, bool exceptionDetails, bool afterCommit)
    {
        _notification = notificationType.FullName ?? notificationType.Name;
        _metrics = metrics;
        _exceptionDetails = exceptionDetails;
        _activity = CqrsDiagnostics.ActivitySource.StartActivity(notificationType.Name);
        if (_activity is not null)
        {
            _activity.SetTag(CqrsDiagnostics.NotificationTag, _notification);
            _activity.SetTag(CqrsDiagnostics.KindTag, "notification");
            _activity.SetTag("cqrs.after_commit", afterCommit);
        }
    }

    public static NotificationTelemetry Start(INotification notification, IServiceProvider serviceProvider, bool afterCommit) =>
        new(notification.GetType(), serviceProvider.GetService<CqrsMetrics>(),
            serviceProvider.GetService<CqrsOptions>()?.RecordExceptionDetailsInTraces ?? false, afterCommit);

    /// <summary>
    /// Falha de um handler. Na publicação imediata a exceção segue para quem publicou; na pós-commit, os demais handlers
    /// continuam e a publicação termina como <c>exception</c>.
    /// </summary>
    public void HandlerFailed(Exception exception)
    {
        _firstErrorType ??= exception.GetType().FullName ?? exception.GetType().Name;
        if (_activity is not null)
        {
            _activity.RecordException(exception, _exceptionDetails);
            _activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }
    }

    /// <summary>Publicação encerrada pelo token de quem publicou: não é erro do sistema.</summary>
    public void Cancel() => Complete(Canceled);

    /// <summary>Publicação concluída: <c>exception</c> se algum handler falhou, senão <c>success</c>.</summary>
    public void Complete() => Complete(_firstErrorType is null ? Success : ExceptionOutcome);

    private void Complete(string outcome)
    {
        var errorType = outcome == ExceptionOutcome ? _firstErrorType : null;
        if (_activity is not null)
        {
            _activity.SetTag(CqrsDiagnostics.OutcomeTag, outcome);
            if (errorType is not null)
                _activity.SetTag(CqrsDiagnostics.ErrorTypeTag, errorType);
        }

        if (_metrics is { NotificationsEnabled: true })
            _metrics.RecordNotification(_notification, outcome, errorType, Stopwatch.GetElapsedTime(_start));
    }

    public void Dispose() => _activity?.Dispose();
}
