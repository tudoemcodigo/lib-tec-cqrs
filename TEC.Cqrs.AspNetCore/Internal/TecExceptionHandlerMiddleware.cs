#if !NET10_0_OR_GREATER
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace TEC.Cqrs.AspNetCore.Internal;

/// <summary>
/// Tratamento global de exceções para o .NET 8, com o mesmo fluxo do <c>ExceptionHandlerMiddleware</c> do ASP.NET Core.
/// </summary>
/// <remarks>
/// O <c>ExceptionHandlerOptions.SuppressDiagnosticsCallback</c> (que evita registrar de novo erros do cliente e exceções já
/// registradas pelo pipeline) só existe a partir do .NET 10; no .NET 8 o middleware padrão registra toda exceção. Este
/// middleware reproduz o comportamento do .NET 10: cancelamento pelo cliente vira 499 sem log de erro, resposta já iniciada
/// propaga a exceção, a resposta é limpa (sem cache, sem endpoint) e o <see cref="IExceptionHandlerFeature"/> é preenchido.
/// </remarks>
internal static partial class TecExceptionHandlerMiddleware
{
    public const string Category = "TEC.Cqrs.AspNetCore.ExceptionHandler";

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, ILogger logger,
        Func<Exception, IServiceProvider?, bool> suppressDiagnostics, Func<HttpContext, Task> handler)
    {
        ExceptionDispatchInfo edi;
        try
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            edi = ExceptionDispatchInfo.Capture(ex);
        }

        var exception = edi.SourceException;
        if (exception is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            RequestAborted(logger);
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            return;
        }

        if (!suppressDiagnostics(exception, context.RequestServices))
            UnhandledException(logger, exception);

        if (context.Response.HasStarted)
        {
            ResponseStarted(logger);
            edi.Throw();
        }

        try
        {
            var feature = new ExceptionHandlerFeature
            {
                Error = exception,
                Path = context.Request.Path.Value ?? string.Empty,
                Endpoint = context.GetEndpoint(),
                RouteValues = context.Features.Get<IRouteValuesFeature>()?.RouteValues
            };

            context.Response.Clear();
            context.SetEndpoint(endpoint: null);
            if (context.Features.Get<IRouteValuesFeature>() is { } routeValues)
                routeValues.RouteValues = null!;

            context.Features.Set<IExceptionHandlerFeature>(feature);
            context.Features.Set<IExceptionHandlerPathFeature>(feature);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.OnStarting(static state =>
            {
                var headers = ((HttpResponse)state).Headers;
                headers.CacheControl = "no-cache,no-store";
                headers.Pragma = "no-cache";
                headers.Expires = "-1";
                headers.ETag = default;
                return Task.CompletedTask;
            }, context.Response);

            await handler(context).ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // Mesmo comportamento do middleware do ASP.NET Core: a falha do handler é registrada e a exceção original propaga
        catch (Exception handlerException)
#pragma warning restore CA1031
        {
            HandlerFailed(logger, handlerException);
        }

        edi.Throw();
    }

    [LoggerMessage(1012, LogLevel.Error, "Exceção não tratada durante a execução da requisição.")]
    private static partial void UnhandledException(ILogger logger, Exception exception);

    [LoggerMessage(1013, LogLevel.Debug, "Requisição cancelada pelo cliente.")]
    private static partial void RequestAborted(ILogger logger);

    [LoggerMessage(1014, LogLevel.Warning, "A resposta já foi iniciada; o tratamento de exceções não será executado.")]
    private static partial void ResponseStarted(ILogger logger);

    [LoggerMessage(1015, LogLevel.Error, "Falha ao executar o tratamento de exceções.")]
    private static partial void HandlerFailed(ILogger logger, Exception exception);
}
#endif
