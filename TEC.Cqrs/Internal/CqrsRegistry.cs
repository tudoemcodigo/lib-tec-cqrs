using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.DependencyInjection;

namespace TEC.Cqrs.Internal;

/// <summary>
/// O que o <c>AddTecCqrs</c> registrou, em forma utilizável em execução sem reflexão (Singleton): o executor de cada
/// requisição, os tipos (concretos, base e interfaces) que têm notification handlers ou authorizers e, por requisição
/// conhecida, os authorizers que se aplicam a ela (calculados no registro).
/// </summary>
internal sealed class CqrsRegistry
{
    // Somente leitura após a construção: FrozenDictionary otimiza a busca por Send (caminho quente)
    private readonly FrozenDictionary<(Type Request, Type Response), RequestHandlerWrapper> _handlers;
    private readonly NotificationTarget[] _notificationTargets;
    private readonly AuthorizerTarget[] _authorizerTargets;
    private readonly FrozenDictionary<Type, AuthorizerTarget[]> _authorizersByRequest;
    private readonly ConcurrentDictionary<(Type Request, Type Response), RequestHandlerWrapper> _dynamicHandlers = new();
    private readonly ConcurrentDictionary<(Type Concrete, Type Static), NotificationTarget[]> _notificationCache = new();
    private readonly ConcurrentDictionary<Type, AuthorizerTarget[]> _authorizerCache = new();
    private readonly CqrsOptions _options;
    private readonly Lock _lateCheckLock = new();

    // Container do AddTecCqrs, guardado até a verificação tardia dos authorizers (EnsureLateAuthorizersAreApplied)
    private IServiceCollection? _pendingServices;
    private string? _lateAuthorizerError;

    /// <param name="options">Opções do <c>AddTecCqrs</c>, já configuradas.</param>
    /// <param name="services">
    /// Container do <c>AddTecCqrs</c>, para a verificação tardia dos authorizers; <c>null</c> dispensa a verificação.
    /// </param>
    public CqrsRegistry(CqrsOptions options, IServiceCollection? services = null)
    {
        _options = options;
        _pendingServices = services;
        _handlers = options.Handlers
            .GroupBy(h => (h.RequestType, h.ResponseType))
            .ToFrozenDictionary(g => g.Key, g => g.First().Wrapper);
        _notificationTargets = [.. options.NotificationHandlers.Select(n => n.Target).DistinctBy(t => t.TargetType)];
        _authorizerTargets = [.. options.Authorizers.Select(a => a.Target).DistinctBy(t => t.TargetType)];

        // Pré-cálculo no registro: para cada requisição conhecida, os alvos registrados de tipo base ou interface que ela
        // implementa, já na ordem de execução (sem GetInterfaces/MakeGenericType em execução)
        _authorizersByRequest = ServiceCollectionExtensions.GetKnownRequests(options)
            .Distinct()
            .ToFrozenDictionary(request => request, request => TargetOrder.Sort(_authorizerTargets, t => t.TargetType, request));
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

    /// <summary>
    /// Authorizers aplicáveis à requisição (tipo exato, classes base e interfaces), na ordem de execução: os alvos
    /// registrados pelo <c>AddTecCqrs</c> que a requisição implementa (pré-calculados no registro para as requisições
    /// conhecidas) mais o tipo exato, que cobre authorizers do tipo exato registrados direto no container.
    /// </summary>
    /// <remarks>
    /// Sem reflexão e sem criar tipos genéricos em execução (compatível com Native AOT). Authorizer de tipo base ou
    /// interface registrado direto no container, fora do <c>AddTecCqrs</c>, nunca entra aqui: na primeira chamada,
    /// <see cref="EnsureLateAuthorizersAreApplied"/> derruba a execução com <see cref="InvalidOperationException"/> (fail
    /// closed) em vez de rodar a requisição sem ele.
    /// </remarks>
    public AuthorizerTarget[] GetAuthorizerTargets<TRequest>()
        where TRequest : IBaseRequest
    {
        if (_authorizerCache.TryGetValue(typeof(TRequest), out var cached))
            return cached;

        EnsureLateAuthorizersAreApplied();
        var registered = _authorizersByRequest.TryGetValue(typeof(TRequest), out var known)
            ? known
            : TargetOrder.Sort(_authorizerTargets, t => t.TargetType, typeof(TRequest));

        // O tipo exato fica sempre primeiro (TargetOrder); DistinctBy mantém um único alvo por tipo
        return _authorizerCache.GetOrAdd(typeof(TRequest),
            TargetOrder.Sort(registered.Prepend(AuthorizerTarget<TRequest>.Instance), t => t.TargetType, typeof(TRequest)));
    }

    /// <summary>
    /// Repete, uma vez e sobre o container completo, a verificação de authorizers do <c>AddTecCqrs</c>: um
    /// <c>IRequestAuthorizer</c> de tipo base ou interface registrado direto no container depois do <c>AddTecCqrs</c>
    /// contaria como autorização declarada, mas nunca seria executado. Chamado na primeira autorização e na subida do
    /// host (<c>.AddAspNetCore()</c>). Depois de falhar, continua falhando com a mesma mensagem.
    /// </summary>
    /// <exception cref="InvalidOperationException">Authorizer que o pipeline nunca executaria.</exception>
    public void EnsureLateAuthorizersAreApplied()
    {
        if (Volatile.Read(ref _pendingServices) is not null)
        {
            lock (_lateCheckLock)
            {
                if (_pendingServices is { } services)
                {
                    try
                    {
                        ServiceCollectionExtensions.EnsureAuthorizersAreApplied(services, _options);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _lateAuthorizerError = ex.Message;
                    }

                    // Solta a referência ao container só depois de gravar o resultado
                    Volatile.Write(ref _pendingServices, null);
                }
            }
        }

        if (_lateAuthorizerError is { } error)
            throw new InvalidOperationException(error);
    }

    private static RequestHandlerWrapper CreateDynamicHandler((Type Request, Type Response) key)
    {
        // A verificação fica aqui, antes de qualquer reflexão: o analisador de AOT a reconhece (sem supressão de IL3050)
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException(
                $"Nenhum handler registrado para '{key.Request.FullName}'. Em Native AOT, registre o handler pelo AddTecCqrs " +
                "(AddCommandHandler/AddQueryHandler).");
        }

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
