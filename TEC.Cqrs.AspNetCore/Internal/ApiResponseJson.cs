using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Serialization;
using TEC.Core.Responses;

namespace TEC.Cqrs.AspNetCore.Internal;

/// <summary>Metadados gerados em tempo de compilação do envelope sem dados (falhas e exception handler).</summary>
[JsonSerializable(typeof(ApiResponse))]
internal sealed partial class CqrsJsonContext : JsonSerializerContext;

/// <summary>Opções JSON configuradas pelo <c>AddAspNetCore</c> (Singleton).</summary>
internal sealed class ApiResponseJsonOptions(JsonSerializerOptions options)
{
    public JsonSerializerOptions Options { get; } = options;

    /// <summary>Convenções do TEC.Core com os metadados da aplicação e os do envelope sem dados.</summary>
    public static ApiResponseJsonOptions Create(IJsonTypeInfoResolver? resolver)
    {
        var options = JsonDefaults.CreateOptions(resolver is null
            ? CqrsJsonContext.Default
            : JsonTypeInfoResolver.Combine(resolver, CqrsJsonContext.Default));
        options.MakeReadOnly();
        return new ApiResponseJsonOptions(options);
    }
}

/// <summary>Escolha das opções JSON e serialização das respostas.</summary>
internal static class ApiResponseJson
{
    private static JsonSerializerOptions? _default;

    /// <summary>
    /// Opções da resposta: as do <c>AddAspNetCore</c> (com <see cref="CqrsAspNetCoreOptions.JsonTypeInfoResolver"/>) ou, sem
    /// configuração, o <see cref="JsonDefaults.Options"/> (reflexão) quando a reflexão do System.Text.Json está habilitada.
    /// </summary>
    public static JsonSerializerOptions Resolve(HttpContext httpContext) =>
        httpContext.RequestServices?.GetService<ApiResponseJsonOptions>()?.Options ?? Default;

    private static JsonSerializerOptions Default => _default ??= JsonSerializer.IsReflectionEnabledByDefault
        ? ReflectionOptions()
        : ApiResponseJsonOptions.Create(resolver: null).Options;

    /// <summary>Metadados do tipo real da resposta, com mensagem clara se o tipo não estiver no contexto JSON.</summary>
    public static JsonTypeInfo GetTypeInfo(JsonSerializerOptions options, Type type)
    {
        try
        {
            return options.GetTypeInfo(type);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(
                $"Sem metadados JSON para '{type}'. Em apps com trimming ou Native AOT, registre o tipo em um " +
                "JsonSerializerContext ([JsonSerializable(typeof(...))]) e informe-o em " +
                ".AddAspNetCore(http => http.JsonTypeInfoResolver = AppJsonContext.Default).", ex);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Usado apenas quando JsonSerializer.IsReflectionEnabledByDefault é true (desligado em apps com trimming/AOT).")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Usado apenas quando JsonSerializer.IsReflectionEnabledByDefault é true (desligado em apps com trimming/AOT).")]
    private static JsonSerializerOptions ReflectionOptions() => JsonDefaults.Options;
}
