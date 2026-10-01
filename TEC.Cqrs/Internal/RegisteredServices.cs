using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Consultas sobre os serviços já registrados no container (sem varrer assemblies e sem criar tipos genéricos: compatível
/// com trimming/Native AOT). Usadas nas verificações de inicialização e nos diagnósticos, inclusive pelos pacotes
/// <c>TEC.Cqrs.FluentValidation</c> e <c>TEC.Cqrs.AspNetCore</c>.
/// </summary>
internal static class RegisteredServices
{
    /// <summary>
    /// Argumentos dos serviços genéricos fechados de <paramref name="openGeneric"/> registrados, na posição
    /// <paramref name="argumentIndex"/> (ex.: os <c>T</c> dos <c>IRequestAuthorizer&lt;T&gt;</c>).
    /// </summary>
    public static HashSet<Type> GetGenericArguments(IServiceCollection services, Type openGeneric, int argumentIndex = 0) =>
    [
        .. services
            .Select(d => d.ServiceType)
            .Where(t => t.IsConstructedGenericType && t.GetGenericTypeDefinition() == openGeneric)
            .Select(t => t.GetGenericArguments()[argumentIndex])
    ];

    /// <summary>
    /// Requisições conhecidas: as que têm <see cref="IRequestHandler{TRequest, TResponse}"/> registrado no container e as
    /// informadas em <paramref name="additionalRequests"/> (ex.: encontradas na varredura, mesmo sem handler).
    /// </summary>
    public static HashSet<Type> GetRequests(IServiceCollection services, IEnumerable<Type> additionalRequests) =>
        [.. GetGenericArguments(services, typeof(IRequestHandler<,>)), .. additionalRequests];

    /// <summary>
    /// Nome da classe registrada para o serviço genérico fechado de <paramref name="openGeneric"/> com o argumento
    /// <paramref name="argument"/> (para mensagens de erro); <c>null</c> se registrado por factory ou instância.
    /// </summary>
    public static string? FindImplementationName(IServiceCollection services, Type openGeneric, Type argument) =>
        services.FirstOrDefault(d => d.ServiceType.IsConstructedGenericType
            && d.ServiceType.GetGenericTypeDefinition() == openGeneric
            && d.ServiceType.GetGenericArguments()[0] == argument)?.ImplementationType?.Name;
}
