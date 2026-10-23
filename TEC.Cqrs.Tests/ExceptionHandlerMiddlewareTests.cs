#if !NET10_0_OR_GREATER
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TEC.Cqrs.AspNetCore.Internal;
using TEC.Cqrs.Tests.Fakes;

namespace TEC.Cqrs.Tests;

/// <summary>Middleware de exceções próprio do .NET 8 (no .NET 10 o <c>UseTecExceptionHandler</c> usa o do ASP.NET Core).</summary>
public class ExceptionHandlerMiddlewareTests
{
    private sealed class Run
    {
        public ListLoggerProvider Logs { get; } = new();

        public DefaultHttpContext Context { get; } = new();

        public bool HandlerCalled { get; private set; }

        public Task InvokeAsync(Exception exception, Func<HttpContext, Task>? handler = null, bool suppress = false) =>
            TecExceptionHandlerMiddleware.InvokeAsync(
                Context,
                _ => throw exception,
                Logs.CreateLogger(TecExceptionHandlerMiddleware.Category),
                (_, _) => suppress,
                context =>
                {
                    HandlerCalled = true;
                    return handler?.Invoke(context) ?? Task.CompletedTask;
                });

        public int Count(int eventId) => Logs.Entries.Count(e => e.EventId.Id == eventId);
    }

    /// <summary>Resposta já iniciada (cabeçalhos enviados).</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Aborted_client_gets_499_without_error_log(bool ioException)
    {
        var run = new Run();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        run.Context.RequestAborted = cts.Token;

        await run.InvokeAsync(ioException ? new IOException("conexão encerrada") : new OperationCanceledException(cts.Token));

        await Assert.That(run.Context.Response.StatusCode).IsEqualTo(StatusCodes.Status499ClientClosedRequest);
        await Assert.That(run.Count(1013)).IsEqualTo(1);
        await Assert.That(run.Count(1012)).IsEqualTo(0);
        await Assert.That(run.HandlerCalled).IsFalse();
    }

    [Test]
    public async Task Cancellation_without_client_abort_is_treated_as_error()
    {
        var run = new Run();

        await run.InvokeAsync(new OperationCanceledException());

        await Assert.That(run.Context.Response.StatusCode).IsEqualTo(StatusCodes.Status500InternalServerError);
        await Assert.That(run.Count(1012)).IsEqualTo(1);
        await Assert.That(run.HandlerCalled).IsTrue();
    }

    [Test]
    public async Task Already_started_response_propagates_the_original_exception()
    {
        var run = new Run();
        run.Context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var original = new InvalidOperationException("bug");

        var ex = await Assert.That(() => run.InvokeAsync(original)).ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex).IsSameReferenceAs(original);
        await Assert.That(run.Count(1012)).IsEqualTo(1);
        await Assert.That(run.Count(1014)).IsEqualTo(1);
        await Assert.That(run.HandlerCalled).IsFalse();
    }

    [Test]
    public async Task Failure_in_handling_itself_is_logged_and_propagates_original_exception()
    {
        var run = new Run();
        var original = new InvalidOperationException("bug");

        var ex = await Assert.That(() => run.InvokeAsync(original, _ => throw new FormatException("falha ao serializar")))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex).IsSameReferenceAs(original);
        await Assert.That(run.Logs.Entries.Single(e => e.EventId.Id == 1015).Exception).IsTypeOf<FormatException>();
    }

    [Test]
    public async Task Handling_fills_the_feature_and_clears_the_response()
    {
        var run = new Run();
        var original = new InvalidOperationException("bug");
        Exception? seen = null;

        await run.InvokeAsync(original, context =>
        {
            seen = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            return Task.CompletedTask;
        });

        await Assert.That(seen).IsSameReferenceAs(original);
        await Assert.That(run.Context.Response.StatusCode).IsEqualTo(StatusCodes.Status500InternalServerError);
        await Assert.That(run.Context.GetEndpoint()).IsNull();
    }

    [Test]
    public async Task Already_logged_exception_is_not_logged_again()
    {
        var run = new Run();

        await run.InvokeAsync(new InvalidOperationException("bug"), suppress: true);

        await Assert.That(run.Count(1012)).IsEqualTo(0);
        await Assert.That(run.HandlerCalled).IsTrue();
    }
}
#endif
