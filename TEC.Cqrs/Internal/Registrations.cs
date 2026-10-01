using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;

namespace TEC.Cqrs.Internal;

/// <summary>Handler de requisição a registrar: o serviço do container e o executor genérico (criado sem reflexão no registro explícito).</summary>
internal sealed record RequestHandlerRegistration(Type RequestType, Type ResponseType, ServiceDescriptor Descriptor,
    RequestHandlerWrapper Wrapper);

/// <summary>Notification handler a registrar e o tipo de notificação que ele trata.</summary>
internal sealed record NotificationHandlerRegistration(ServiceDescriptor Descriptor, NotificationTarget Target);

/// <summary>Authorizer a registrar e o tipo de requisição (concreto, base ou interface) que ele autoriza.</summary>
internal sealed record AuthorizerRegistration(ServiceDescriptor Descriptor, AuthorizerTarget Target);

/// <summary>
/// Tipo para o qual há <c>INotificationHandler&lt;T&gt;</c> registrado. Resolve e chama os handlers sem reflexão
/// (a instância genérica é criada no registro), permitindo entregar notificações a handlers de tipo base ou interface
/// também em Native AOT.
/// </summary>
internal abstract class NotificationTarget(Type targetType)
{
    public Type TargetType { get; } = targetType;

    public abstract IEnumerable<object?> Resolve(IServiceProvider serviceProvider);

    public abstract Task Invoke(object handler, INotification notification, CancellationToken cancellationToken);
}

internal sealed class NotificationTarget<TNotification>() : NotificationTarget(typeof(TNotification))
    where TNotification : INotification
{
    public static readonly NotificationTarget<TNotification> Instance = new();

    public override IEnumerable<object?> Resolve(IServiceProvider serviceProvider) =>
        serviceProvider.GetServices<INotificationHandler<TNotification>>();

    // A conversão via object funciona também para notificações struct
    public override Task Invoke(object handler, INotification notification, CancellationToken cancellationToken) =>
        ((INotificationHandler<TNotification>)handler).Handle((TNotification)notification, cancellationToken);
}

/// <summary>Tipo para o qual há <c>IRequestAuthorizer&lt;T&gt;</c> registrado (mesma ideia do <see cref="NotificationTarget"/>).</summary>
internal abstract class AuthorizerTarget(Type targetType)
{
    public Type TargetType { get; } = targetType;

    public abstract IEnumerable<object?> Resolve(IServiceProvider serviceProvider);

    public abstract Task<Result> Invoke(object authorizer, object request, CancellationToken cancellationToken);
}

internal sealed class AuthorizerTarget<TRequest>() : AuthorizerTarget(typeof(TRequest))
    where TRequest : IBaseRequest
{
    public static readonly AuthorizerTarget<TRequest> Instance = new();

    public override IEnumerable<object?> Resolve(IServiceProvider serviceProvider) =>
        serviceProvider.GetServices<IRequestAuthorizer<TRequest>>();

    public override Task<Result> Invoke(object authorizer, object request, CancellationToken cancellationToken) =>
        ((IRequestAuthorizer<TRequest>)authorizer).AuthorizeAsync((TRequest)request, cancellationToken);
}

/// <summary>Ordem de execução dos handlers/authorizers de tipo base ou interface.</summary>
internal static class TargetOrder
{
    /// <summary>
    /// Ordem determinística dos alvos aplicáveis a <paramref name="concreteType"/>: o próprio tipo, as classes base (da mais
    /// próxima para a mais distante) e as interfaces (por nome completo). Alvos repetidos são removidos.
    /// </summary>
    public static T[] Sort<T>(IEnumerable<T> targets, Func<T, Type> targetType, Type concreteType) =>
        [.. targets
            .Where(t => targetType(t).IsAssignableFrom(concreteType))
            .DistinctBy(targetType)
            .OrderBy(t => Rank(targetType(t), concreteType))
            .ThenBy(t => targetType(t).IsInterface ? targetType(t).FullName ?? targetType(t).Name : string.Empty, StringComparer.Ordinal)];

    private static int Rank(Type target, Type concreteType)
    {
        if (target.IsInterface)
            return int.MaxValue;

        int distance = 0;
        for (var current = concreteType; current is not null && current != target; current = current.BaseType)
            distance++;
        return distance;
    }
}
