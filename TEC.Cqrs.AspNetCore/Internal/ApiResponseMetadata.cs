using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using TEC.Core.Common.Results;
using TEC.Core.Responses;

namespace TEC.Cqrs.AspNetCore.Internal;

/// <summary>Metadados OpenAPI (tipos de resposta e status) dos resultados HTTP do TEC.Cqrs.</summary>
internal static class ApiResponseMetadata
{
    private static readonly string[] JsonContentTypes = ["application/json"];

    /// <summary>Status possíveis de falha: os de cada <see cref="ErrorType"/>, todos com o envelope <see cref="ApiResponse"/>.</summary>
    private static readonly int[] FailureStatusCodes =
        [.. Enum.GetValues<ErrorType>().Select(t => t.ToHttpStatusCode()).Distinct().Order()];

    /// <summary>Adiciona o status de sucesso (com o envelope do endpoint) e os de falha.</summary>
    public static void Populate(EndpointBuilder builder, int successStatusCode, Type successType)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ResponseTypeMetadata(successStatusCode, successType));
        foreach (int statusCode in FailureStatusCodes)
            builder.Metadata.Add(new ResponseTypeMetadata(statusCode, typeof(ApiResponse)));
    }

    private sealed class ResponseTypeMetadata(int statusCode, Type type) : IProducesResponseTypeMetadata
    {
        public Type? Type { get; } = type;

        public int StatusCode { get; } = statusCode;

        public IEnumerable<string> ContentTypes => JsonContentTypes;
    }
}
