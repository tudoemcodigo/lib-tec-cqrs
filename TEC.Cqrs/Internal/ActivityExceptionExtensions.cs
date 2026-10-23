using System.Diagnostics;

namespace TEC.Cqrs.Internal;

/// <summary>Registro de exceções nas <see cref="Activity"/>s do pipeline (requisições e notificações).</summary>
internal static class ActivityExceptionExtensions
{
    /// <summary>
    /// Adiciona o evento <c>exception</c> (convenção do OpenTelemetry). Sem <paramref name="includeDetails"/>, o evento leva
    /// só <c>exception.type</c>: a mensagem e o stack trace (de drivers, HTTP etc.) podem conter dados pessoais ou tokens, e
    /// o backend de traces costuma ter acesso mais amplo que o de logs. Com <paramref name="includeDetails"/>
    /// (<c>CqrsOptions.RecordExceptionDetailsInTraces</c>), leva também <c>exception.message</c> e <c>exception.stacktrace</c>.
    /// </summary>
    public static void RecordException(this Activity activity, Exception exception, bool includeDetails)
    {
        if (includeDetails)
        {
            activity.AddException(exception);
            return;
        }

        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName }
        }));
    }
}
