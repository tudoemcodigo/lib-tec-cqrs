using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Core.Responses;
using TEC.Cqrs.AspNetCore.Internal;

namespace TEC.Cqrs.AspNetCore;

/// <summary>
/// Resposta HTTP com o envelope <see cref="ApiResponse"/> do TEC.Core. Funciona em Minimal APIs (<see cref="IResult"/>)
/// e em Controllers (<see cref="IActionResult"/>). Criada pelos métodos de <see cref="ResultHttpExtensions"/>.
/// </summary>
/// <remarks>
/// <para>O JSON segue as convenções do <c>JsonDefaults</c> do TEC.Core (camelCase, ignora nulos, acentos sem escape),
/// garantindo o mesmo formato em todas as APIs; em apps com trimming/Native AOT, informe os metadados em
/// <see cref="CqrsAspNetCoreOptions.JsonTypeInfoResolver"/>. Respostas de falha recebem o <c>traceId</c> da requisição
/// para correlação com os logs.</para>
/// <para>Implementa <see cref="IEndpointMetadataProvider"/>: o OpenAPI do endpoint descreve o status de sucesso (200) com
/// <typeparamref name="TResponse"/> e os status de falha (400, 401, 403, 404, 409, 422, 429, 500 e 502) com
/// <see cref="ApiResponse"/>.</para>
/// </remarks>
public sealed class ApiResponseHttpResult<TResponse> : IResult, IActionResult, IStatusCodeHttpResult, IValueHttpResult,
    IValueHttpResult<TResponse>, IContentTypeHttpResult, IEndpointMetadataProvider
    where TResponse : ApiResponse
{
    internal ApiResponseHttpResult(TResponse value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>Envelope que será serializado.</summary>
    public TResponse Value { get; }

    object? IValueHttpResult.Value => Value;

    /// <summary>Status HTTP (o mesmo do envelope).</summary>
    public int StatusCode => Value.StatusCode;

    int? IStatusCodeHttpResult.StatusCode => StatusCode;

    /// <summary>Tipo de conteúdo da resposta.</summary>
    public string ContentType => ApiResponseWriter.ContentType;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext) => ApiResponseWriter.WriteAsync(httpContext, Value, location: null, locationRejected: false);

    /// <inheritdoc />
    public Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteAsync(context.HttpContext);
    }

    /// <inheritdoc />
    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        ApiResponseMetadata.Populate(builder, StatusCodes.Status200OK, typeof(TResponse));
}

/// <summary>
/// Resposta HTTP 201 (recurso criado) com o envelope <see cref="ApiResponse{T}"/> e o cabeçalho <c>Location</c>; em falha,
/// o status e os erros conforme o tipo do erro. Criada por <see cref="ResultHttpExtensions.ToCreatedHttpResult{T}(TEC.Core.Common.Results.Result{T}, Func{T, string}, string?)"/>.
/// </summary>
/// <remarks>
/// Implementa <see cref="IEndpointMetadataProvider"/>: o OpenAPI do endpoint descreve o status 201 com
/// <see cref="ApiResponse{T}"/> e os status de falha com <see cref="ApiResponse"/>.
/// </remarks>
/// <typeparam name="T">Tipo dos dados do recurso criado.</typeparam>
public sealed class ApiResponseCreatedHttpResult<T> : IResult, IActionResult, IStatusCodeHttpResult, IValueHttpResult,
    IValueHttpResult<ApiResponse<T>>, IContentTypeHttpResult, IEndpointMetadataProvider
{
    internal ApiResponseCreatedHttpResult(ApiResponse<T> value, string? location = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Location = value.Success ? location : null;
    }

    /// <summary>Envelope que será serializado.</summary>
    public ApiResponse<T> Value { get; }

    object? IValueHttpResult.Value => Value;

    /// <summary>Status HTTP (o mesmo do envelope: 201 em sucesso).</summary>
    public int StatusCode => Value.StatusCode;

    int? IStatusCodeHttpResult.StatusCode => StatusCode;

    /// <summary>Cabeçalho <c>Location</c> (somente em sucesso e se a URL for um caminho relativo seguro).</summary>
    public string? Location { get; }

    /// <summary>A URL informada para o <c>Location</c> não era um caminho relativo seguro e foi descartada (registrado em log).</summary>
    internal bool LocationRejected { get; init; }

    /// <summary>Tipo de conteúdo da resposta.</summary>
    public string ContentType => ApiResponseWriter.ContentType;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext) => ApiResponseWriter.WriteAsync(httpContext, Value, Location, LocationRejected);

    /// <inheritdoc />
    public Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteAsync(context.HttpContext);
    }

    /// <inheritdoc />
    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder) =>
        ApiResponseMetadata.Populate(builder, StatusCodes.Status201Created, typeof(ApiResponse<T>));
}

/// <summary>Escrita do envelope na resposta HTTP.</summary>
internal static class ApiResponseWriter
{
    public const string ContentType = "application/json; charset=utf-8";

    public static Task WriteAsync(HttpContext httpContext, ApiResponse value, string? location, bool locationRejected)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = value;
        if (!response.Success && response.TraceId is null)
            response = response with { TraceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier };

        // Serializa pelo tipo real, para incluir "data" e "pagination" das respostas derivadas
        var options = ApiResponseJson.Resolve(httpContext);
        var typeInfo = ApiResponseJson.GetTypeInfo(options, response.GetType());

        httpContext.Response.StatusCode = response.StatusCode;
        if (location is not null)
            httpContext.Response.Headers.Location = location;
        else if (locationRejected && httpContext.RequestServices?.GetService<ILoggerFactory>() is { } loggerFactory)
            AspNetCoreLog.InvalidLocation(loggerFactory.CreateLogger(AspNetCoreLog.Category));

        return httpContext.Response.WriteAsJsonAsync(response, typeInfo, ContentType, httpContext.RequestAborted);
    }
}
