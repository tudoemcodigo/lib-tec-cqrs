using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using TEC.Core.Common.Results;
using TEC.Core.Responses;
using TEC.Cqrs.AspNetCore.Internal;

namespace TEC.Cqrs.AspNetCore;

/// <summary>
/// Resposta HTTP 204 (sem corpo) em caso de sucesso; em caso de falha, devolve o envelope <see cref="ApiResponse"/>
/// padronizado com o status HTTP correspondente ao <see cref="ErrorType"/> do erro.
/// </summary>
/// <remarks>
/// Funciona tanto em Minimal APIs (<see cref="IResult"/>) quanto em Controllers (<see cref="IActionResult"/>).
/// Implementa <see cref="IEndpointMetadataProvider"/> para documentar o status 204 e os status de falha no OpenAPI.
/// </remarks>
public sealed class ApiResponseNoContentHttpResult : IResult, IActionResult, IStatusCodeHttpResult, IEndpointMetadataProvider
{
    private readonly Result _result;

    // Envelope de falha montado uma vez: StatusCode e ExecuteAsync usam o mesmo mapeamento do ApiResponse.FromResult
    // (ex.: um erro interno em qualquer posição da lista vira 500, não o status do primeiro erro)
    private readonly ApiResponse? _failure;

    internal ApiResponseNoContentHttpResult(Result result)
    {
        _result = result;
        _failure = result.IsSuccess ? null : ApiResponse.FromResult(result);
    }

    /// <summary>
    /// Status HTTP resultante: 204 em sucesso; em falha, o mesmo status escrito na resposta pelo
    /// <see cref="ApiResponse.FromResult(Result, string?)"/> (erro interno ou de integração em qualquer posição prevalece).
    /// </summary>
    public int StatusCode => _failure?.StatusCode ?? StatusCodes.Status204NoContent;

    int? IStatusCodeHttpResult.StatusCode => StatusCode;

    /// <summary>Indica se a operação foi bem-sucedida.</summary>
    public bool IsSuccess => _result.IsSuccess;

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (_failure is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        return ApiResponseWriter.WriteAsync(httpContext, _failure, location: null, locationRejected: false);
    }

    /// <inheritdoc />
    public Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteAsync(context.HttpContext);
    }

    /// <inheritdoc />
    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Metadata.Add(new NoContentResponseTypeMetadata());
        foreach (int statusCode in ApiResponseMetadataFailureStatusCodes())
            builder.Metadata.Add(new ErrorResponseTypeMetadata(statusCode));
    }

    private static IEnumerable<int> ApiResponseMetadataFailureStatusCodes() =>
        Enum.GetValues<ErrorType>().Select(t => t.ToHttpStatusCode()).Distinct().Order();

    private sealed class NoContentResponseTypeMetadata : IProducesResponseTypeMetadata
    {
        public Type? Type => null;
        public int StatusCode => StatusCodes.Status204NoContent;
        public IEnumerable<string> ContentTypes => [];
    }

    private sealed class ErrorResponseTypeMetadata(int statusCode) : IProducesResponseTypeMetadata
    {
        public Type? Type => typeof(ApiResponse);
        public int StatusCode { get; } = statusCode;
        public IEnumerable<string> ContentTypes => ["application/json"];
    }
}
