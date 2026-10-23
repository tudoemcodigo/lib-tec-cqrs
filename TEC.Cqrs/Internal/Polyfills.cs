// Equivalentes internos de APIs do .NET 9+ usadas pela biblioteca, para o alvo net8.0 (mesmo comportamento).
// System.Threading.Lock vem do polyfill canônico (build/Polyfills/Lock.cs).

#if !NET9_0_OR_GREATER
using System.Diagnostics;

namespace TEC.Cqrs.Internal;

internal static class ActivityPolyfills
{
    /// <summary>
    /// Equivalente ao <c>Activity.AddException</c> (.NET 9+): evento <c>exception</c> com os atributos do OpenTelemetry
    /// (<c>exception.type</c>, <c>exception.message</c>, <c>exception.stacktrace</c>). Usado apenas por
    /// <see cref="ActivityExceptionExtensions.RecordException"/>, com <c>CqrsOptions.RecordExceptionDetailsInTraces</c>.
    /// </summary>
    public static Activity AddException(this Activity activity, Exception exception) =>
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.ToString() }
        }));
}
#endif
