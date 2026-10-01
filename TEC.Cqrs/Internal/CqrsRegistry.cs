using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.DependencyInjection;

namespace TEC.Cqrs.Internal;

/// <summary>
/// O que o <c>AddTecCqrs</c> registrou, em forma utilizável em execução sem reflexão (Singleton): o executor de cada
/// requisição e os tipos (concretos, base e interfaces) que têm notification handlers ou authorizers.
/// </summary>
internal sealed class CqrsRegistry
{
    private readonly Dictionary<(Type Request, Type Response), RequestHandlerWrapper> _handlers;
    private readonly NotificationTarget[] _notificationTargets;
    private readonly AuthorizerTarget[] _authorizerTargets;
    private readonly ConcurrentDictionary<(Type Request, Type Response), RequestHandlerWrapper> _dynamicHandlers = new();
    private readonly ConcurrentDictionary<(Type Concrete, Type Static), NotificationTarget[]> _notificationCache = new();
    private readonly ConcurrentDictionary<Type, AuthorizerTarget[]> _authorizerCache = new();

    public CqrsRegistry(CqrsOptions options)
    {
        _handlers = options.Handlers
            .GroupBy(h => (h.RequestType, h.ResponseType))
            .ToDictionary(g => g.Key, g => g.First().Wrapper);
        _notificationTargets = [.. options.NotificationHandlers.Select(n => n.Target).DistinctBy(t => t.TargetType)];
        _authorizerTargets = [.. options.Authorizers.Select(a => a.Target).DistinctBy(t => t.TargetType)];
    }

    /// <summary>
    /// Executor da requisição. Requisições cujo handler foi registrado direto no container (fora do <c>AddTecCqrs</c>)
    /// têm o executor criado por reflexão em JIT (com as mesmas verificações de marcações do registro); em Native AOT,
    /// lançam <see cref="InvalidOperationException"/>.
    /// </summary>
    public RequestHandlerWrapper<TResponse> GetHandler<TResponse>(Type requestType)
        where TResponse : Result
    {
        if (_handlers.TryGetValue((requestType, typeof(TResponse)), out var wrapper))
            return (RequestHandlerWrapper<TResponse>)wrapper;

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException(
                $"Nenhum handler registrado para '{requestType.FullName}'. Em Native AOT, registre o handler pelo AddTecCqrs " +
                "(AddCommandHandler/AddQueryHandler).");
        }

        return (RequestHandlerWrapper<TResponse>)_dynamicHandlers.GetOrAdd((requestType, typeof(TResponse)), CreateDynamicHandler);
    }

    /// <summary>
    /// Alvos de notification handler aplicáveis ao tipo real da notificação, na ordem de execução. Inclui o tipo estático
    /// usado no <c>Publish</c> (resolvido sem reflexão), para handlers desse tipo registrados direto no container.
    /// </summary>
    public NotificationTarget[] GetNotificationTargets(Type notificationType, NotificationTarget staticTarget) =>
        _notificationCache.GetOrAdd((notificationType, staticTarget.TargetType),
            static (key, state) => TargetOrder.Sort(state.Targets.Append(state.Static), t => t.TargetType, key.Concrete),
            (Targets: _notificationTargets, Static: staticTarget));

    /// <summary>Authorizers aplicáveis à requisição (tipo exato, classes base e interfaces), na ordem de execução.</summary>
    public AuthorizerTarget[] GetAuthorizerTargets<TRequest>()
        where TRequest : IBaseRequest =>
        _authorizerCache.GetOrAdd(typeof(TRequest),
            static (requestType, targets) => TargetOrder.Sort(targets.Append(AuthorizerTarget<TRequest>.Instance), t => t.TargetType, requestType),
            _authorizerTargets);

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Chamado apenas quando RuntimeFeature.IsDynamicCodeSupported é true (verificado em GetHandler).")]
    private static RequestHandlerWrapper CreateDynamicHandler((Type Request, Type Response) key)
    {
        // Handler registrado direto no container: a requisição não passou pela validação do registro (AddTecCqrs)
        RequestMetadata.EnsureValid(key.Request);

        if (key.Response.IsGenericType && key.Response.GetGenericTypeDefinition() == typeof(Result<>))
            ResultFactory.RegisterDynamic(key.Response.GetGenericArguments()[0]);

        // Método genérico conhecido (preservado pelo trimming) em vez de Activator sobre um tipo montado em execução
        return (RequestHandlerWrapper)typeof(CqrsRegistry)
            .GetMethod(nameof(CreateHandler), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(key.Request, key.Response)
            .Invoke(null, null)!;
    }

    private static RequestHandlerWrapper CreateHandler<TRequest, TResponse>()
        where TRequest : IRequest<TResponse>
        where TResponse : Result =>
        new RequestHandlerWrapperImpl<TRequest, TResponse>();
}
