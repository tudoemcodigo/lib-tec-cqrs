using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
#if !NET10_0_OR_GREATER
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Cqrs.AspNetCore.Internal;
#endif
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Core.Responses;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.Internal;

namespace TEC.Cqrs.AspNetCore;

/// <summary>Middlewares do TEC.Cqrs.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Tratamento global de exceções com o envelope <see cref="ApiResponse"/>:
    /// <list type="bullet">
    /// <item>exceções do TEC.Core (<c>AppException</c>) usam o próprio status;</item>
    /// <item>exceções reconhecidas por um <see cref="IExceptionErrorMapper"/> registrado (ex.:
    /// <c>FluentValidation.ValidationException</c>, com o <c>.AddFluentValidation()</c>) viram a resposta dos erros mapeados
    /// (HTTP 400 com um erro por campo, na validação);</item>
    /// <item><see cref="BadHttpRequestException"/> (JSON malformado, corpo grande demais etc.) usa o próprio status 4xx
    /// com mensagem genérica;</item>
    /// <item>qualquer outra vira HTTP 500 com mensagem genérica, sem detalhes internos.</item>
    /// </list>
    /// Erros do servidor são registrados em log pelo middleware padrão do ASP.NET Core; erros do cliente (4xx) não,
    /// para não gerar alertas falsos, e exceções já registradas pelo pipeline do mediator também não (um único log por exceção).
    /// </summary>
    /// <remarks>Registre antes dos demais middlewares, para capturar exceções de toda a aplicação.</remarks>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// app.UseTecExceptionHandler();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseTecExceptionHandler(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
#if NET10_0_OR_GREATER
        return app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = HandleExceptionAsync,
            // NotFoundException (AppException) vira 404 de propósito; sem isto, o ASP.NET Core troca a resposta por exceção
            AllowStatusCode404Response = true,
            SuppressDiagnosticsCallback = context => SuppressDiagnostics(context.Exception, context.HttpContext.RequestServices)
        });
#else
        // .NET 8: o UseExceptionHandler não permite suprimir o log (SuppressDiagnosticsCallback é do .NET 10)
        var logger = app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger(TecExceptionHandlerMiddleware.Category)
            ?? NullLogger.Instance;
        return app.Use(next => context =>
            TecExceptionHandlerMiddleware.InvokeAsync(context, next, logger, SuppressDiagnostics, HandleExceptionAsync));
#endif
    }

    /// <summary>Não registra de novo: erros do cliente (4xx) e exceções já registradas pelo <c>LoggingBehavior</c>.</summary>
    internal static bool SuppressDiagnostics(Exception? exception, IServiceProvider? services = null) =>
        IsClientError(exception, services) || CqrsDiagnostics.IsExceptionLogged(exception);

    internal static Task HandleExceptionAsync(HttpContext httpContext)
    {
        var exception = httpContext.Features.Get<IExceptionHandlerFeature>()?.Error;
        var response = exception switch
        {
            null => ApiResponse.InternalError(),
            AppException ex => ApiResponse.FromException(ex),
            BadHttpRequestException ex => FromBadHttpRequest(ex),
            { } ex when TryMap(ex, httpContext.RequestServices, out var errors) => ApiResponse.FromResult(Result.Failure([.. errors])),
            { } ex => ApiResponse.FromException(ex)
        };

        return new ApiResponseHttpResult<ApiResponse>(response).ExecuteAsync(httpContext);
    }

    /// <summary>Exceções causadas pela requisição do cliente (4xx), que não indicam falha do sistema.</summary>
    internal static bool IsClientError(Exception? exception, IServiceProvider? services = null) => exception switch
    {
        null => false,
        BadHttpRequestException => true,
        AppException ex => ex.ErrorType.IsExposedToClient(),
        { } ex => TryMap(ex, services, out var errors) && errors.All(e => e.Type.IsExposedToClient())
    };

    /// <summary>Converte a exceção pelos <see cref="IExceptionErrorMapper"/> registrados (o primeiro que reconhecer).</summary>
    private static bool TryMap(Exception exception, IServiceProvider? services, out IReadOnlyList<Error> errors) =>
        (services?.GetServices<IExceptionErrorMapper>() ?? []).TryMapException(exception, out errors);

    /// <summary>A mensagem da exceção não é exposta (pode conter nomes de parâmetros e detalhes do framework).</summary>
    private static ApiResponse FromBadHttpRequest(BadHttpRequestException exception)
    {
        int status = exception.StatusCode is >= 400 and <= 499 ? exception.StatusCode : StatusCodes.Status400BadRequest;
        string message = status switch
        {
            StatusCodes.Status408RequestTimeout => "Tempo de envio da requisição esgotado.",
            StatusCodes.Status413PayloadTooLarge => "O corpo da requisição excede o tamanho máximo permitido.",
            StatusCodes.Status415UnsupportedMediaType => "Tipo de conteúdo não suportado.",
            StatusCodes.Status431RequestHeaderFieldsTooLarge => "Os cabeçalhos da requisição excedem o tamanho máximo permitido.",
            _ => "Requisição inválida."
        };

        return ApiResponse.Fail(status, message);
    }
}
