using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Consultas sobre os serviços já registrados no container (sem varrer assemblies e sem criar tipos genéricos: compatível
/// com trimming/Native AOT). Usadas nas verificações de inicialização e nos diagnósticos, inclusive pelos pacotes
/// <c>TEC.Cqrs.FluentValidation</c> e <c>TEC.Cqrs.AspNetCore</c>.
/// </summary>
/// <remarks>
/// Registros keyed são ignorados: o pipeline resolve tudo sem chave, e ler <see cref="ServiceDescriptor.ImplementationType"/>
/// de um registro keyed lança <see cref="InvalidOperationException"/>.
/// </remarks>
internal static class RegisteredServices
{
    /// <summary>
    /// Argumentos dos serviços genéricos fechados de <paramref name="openGeneric"/> registrados, na posição
    /// <paramref name="argumentIndex"/> (ex.: os <c>T</c> dos <c>IRequestAuthorizer&lt;T&gt;</c>).
    /// </summary>
    public static HashSet<Type> GetGenericArguments(IServiceCollection services, Type openGeneric, int argumentIndex = 0) =>
    [
        .. services
            .Where(d => !d.IsKeyedService)
            .Select(d => d.ServiceType)
            .Where(t => t.IsConstructedGenericType && t.GetGenericTypeDefinition() == openGeneric)
            .Select(t => t.GetGenericArguments()[argumentIndex])
    ];

    /// <summary>
    /// Implementações genéricas abertas registradas para <paramref name="openGeneric"/>
    /// (ex.: <c>AddScoped(typeof(IRequestAuthorizer&lt;&gt;), typeof(TenantAuthorizer&lt;&gt;))</c>).
    /// </summary>
    public static Type[] GetOpenGenericImplementations(IServiceCollection services, Type openGeneric) =>
    [
        .. services
            .Where(d => !d.IsKeyedService && d.ServiceType == openGeneric && d.ImplementationType is { IsGenericTypeDefinition: true })
            .Select(d => d.ImplementationType!)
    ];

    /// <summary>
    /// <c>true</c> se o container consegue fechar <paramref name="openImplementation"/> (com um único parâmetro genérico)
    /// com <paramref name="argument"/>, isto é, se o argumento atende às restrições do parâmetro.
    /// </summary>
    /// <remarks>
    /// Verificado sem <c>MakeGenericType</c> (compatível com Native AOT). Restrições que não dá para avaliar assim
    /// (<c>new()</c> ou que dependem do próprio parâmetro, como <c>where T : IComparable&lt;T&gt;</c>) retornam <c>false</c>:
    /// na dúvida, a requisição não conta como autorizada (fail closed).
    /// </remarks>
    public static bool CanClose(Type openImplementation, Type argument)
    {
        var parameters = openImplementation.GetGenericArguments();
        if (parameters.Length != 1)
            return false;

        var parameter = parameters[0];
        var special = parameter.GenericParameterAttributes & GenericParameterAttributes.SpecialConstraintMask;

        if (special.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
            || (special.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint) && argument.IsValueType)
            || (special.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)
                && (!argument.IsValueType || Nullable.GetUnderlyingType(argument) is not null)))
        {
            return false;
        }

        return parameter.GetGenericParameterConstraints()
            .All(constraint => !constraint.ContainsGenericParameters && constraint.IsAssignableFrom(argument));
    }

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
        services.FirstOrDefault(d => !d.IsKeyedService
            && d.ServiceType.IsConstructedGenericType
            && d.ServiceType.GetGenericTypeDefinition() == openGeneric
            && d.ServiceType.GetGenericArguments()[0] == argument)?.ImplementationType?.Name;
}

/// <summary>
/// Os <see cref="IRequestAuthorizer{TRequest}"/> registrados no container, para decidir se uma requisição declara
/// autorização: fechados (do tipo exato, de um tipo base ou de uma interface) e genéricos abertos.
/// </summary>
internal sealed class RegisteredAuthorizers
{
    private readonly HashSet<Type> _targets;
    private readonly Type[] _openImplementations;

    private RegisteredAuthorizers(HashSet<Type> targets, Type[] openImplementations)
    {
        _targets = targets;
        _openImplementations = openImplementations;
    }

    public static RegisteredAuthorizers From(IServiceCollection services) => new(
        RegisteredServices.GetGenericArguments(services, typeof(IRequestAuthorizer<>)),
        RegisteredServices.GetOpenGenericImplementations(services, typeof(IRequestAuthorizer<>)));

    /// <summary>
    /// <c>true</c> se algum authorizer é executado para <paramref name="requestType"/>: um fechado de tipo atribuível ou um
    /// genérico aberto cujas restrições a requisição atende (o container o fecha com o tipo exato dela).
    /// </summary>
    public bool Authorizes(Type requestType) =>
        _targets.Any(target => target.IsAssignableFrom(requestType))
        || _openImplementations.Any(implementation => RegisteredServices.CanClose(implementation, requestType));
}
