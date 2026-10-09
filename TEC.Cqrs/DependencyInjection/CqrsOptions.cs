using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.DependencyInjection;

/// <summary>Configuração do <c>AddTecCqrs</c>: o que registrar e como o pipeline se comporta.</summary>
/// <remarks>
/// <para>Duas formas de registrar handlers, notification handlers, authorizers e validadores, que podem ser combinadas:</para>
/// <list type="bullet">
/// <item><b>Varredura de assemblies</b> (<see cref="RegisterServicesFromAssembly"/>): prática, baseada em reflexão;
/// incompatível com trimming e Native AOT (avisos IL2026/IL3050).</item>
/// <item><b>Registro explícito</b> (<see cref="AddCommandHandler{TCommand, THandler}"/>,
/// <see cref="AddQueryHandler{TQuery, TValue, THandler}"/>, <see cref="AddNotificationHandler{TNotification, THandler}"/>
/// etc.): compatível com trimming e Native AOT.</item>
/// </list>
/// <para>As opções são congeladas ao final do <c>AddTecCqrs</c>: alterar qualquer propriedade ou registrar algo depois
/// disso (por exemplo, na instância obtida do container) lança <see cref="InvalidOperationException"/>, para que a
/// configuração validada na inicialização seja a mesma usada em execução.</para>
/// </remarks>
public sealed class CqrsOptions
{
    private readonly List<Assembly> _assemblies = [];
    private readonly List<BehaviorRegistration> _customBehaviors = [];
    private readonly List<RequestHandlerRegistration> _handlers = [];
    private readonly List<NotificationHandlerRegistration> _notificationHandlers = [];
    private readonly List<AuthorizerRegistration> _authorizers = [];
    private readonly List<ServiceDescriptor> _validators = [];
    private readonly List<Type> _scannedRequests = [];
    private bool _frozen;

    /// <summary>Assemblies informados em <see cref="RegisterServicesFromAssembly"/>, na ordem em que foram adicionados.</summary>
    /// <remarks>Usados também por extensões (ex.: o <c>AddFluentValidation()</c> procura validators nesses assemblies).</remarks>
    public IReadOnlyList<Assembly> Assemblies => _assemblies;

    internal IReadOnlyList<BehaviorRegistration> CustomBehaviors => _customBehaviors;

    internal IReadOnlyList<RequestHandlerRegistration> Handlers => _handlers;

    internal IReadOnlyList<NotificationHandlerRegistration> NotificationHandlers => _notificationHandlers;

    internal IReadOnlyList<AuthorizerRegistration> Authorizers => _authorizers;

    internal IReadOnlyList<ServiceDescriptor> Validators => _validators;

    /// <summary>Requisições encontradas na varredura (inclusive as sem handler), para as verificações de inicialização.</summary>
    internal IReadOnlyList<Type> ScannedRequests => _scannedRequests;

    internal bool HasRegistrations =>
        _assemblies.Count > 0 || _handlers.Count > 0 || _notificationHandlers.Count > 0 || _authorizers.Count > 0 || _validators.Count > 0;

    /// <summary>
    /// Tempo a partir do qual uma requisição é registrada como lenta (log de aviso). Padrão: 500 ms.
    /// <c>null</c> desativa a verificação.
    /// </summary>
    public TimeSpan? SlowRequestThreshold
    {
        get;
        set
        {
            EnsureNotFrozen();
            field = value is null || value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(SlowRequestThreshold), "O limite deve ser maior que zero (ou null para desativar).");
        }
    } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Exige validador (<see cref="IRequestValidator{TRequest}"/>, ex.: um <c>AbstractValidator</c> do pacote
    /// <c>TEC.Cqrs.FluentValidation</c>) em todo command. Command sem validador lança <see cref="InvalidOperationException"/>
    /// ao ser executado, em vez de rodar sem validar a entrada (fail closed). Dispense um command específico com
    /// <see cref="SkipValidationAttribute"/>. Padrão: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Vale mesmo sem nenhum pacote de validação registrado (esquecer o <c>AddFluentValidation()</c> não desliga a
    /// validação em silêncio). Use <c>CqrsDiagnostics.FindCommandsWithoutValidator</c> em um teste para detectar antes de produção.
    /// </remarks>
    public bool RequireValidatorForCommands
    {
        get;
        set
        {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Exige que toda requisição declare sua autorização: <c>[AuthorizeRequest]</c>, um <c>IRequestAuthorizer</c> (do próprio
    /// tipo, de um tipo base ou de uma interface) ou <c>[AllowAnonymousRequest]</c>. Padrão: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>Verificado em dois momentos (fail closed): no <c>AddTecCqrs</c>, que lança <see cref="InvalidOperationException"/>
    /// listando as requisições registradas (e as encontradas na varredura) sem autorização declarada; e em execução, para
    /// requisições cujo handler foi registrado direto no container.</para>
    /// <para>Desative apenas em aplicações sem nenhuma requisição protegida. Use
    /// <c>CqrsDiagnostics.FindRequestsWithoutAuthorization</c> para listar as requisições pendentes.</para>
    /// </remarks>
    public bool RequireAuthorization
    {
        get;
        set
        {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Tipo de claim lido pelo <see cref="ClaimPermissionChecker"/> (o <see cref="IPermissionChecker"/> padrão) nas regras de
    /// <see cref="RequirePermissionAttribute"/>. Padrão: <c>tec_perm</c>, o claim de permissão efetiva do TEC.Security.
    /// </summary>
    /// <exception cref="ArgumentException">Valor nulo, vazio ou só espaços.</exception>
    public string PermissionClaimType
    {
        get;
        set
        {
            EnsureNotFrozen();
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(PermissionClaimType));
            field = value;
        }
    } = DefaultPermissionClaimType;

    /// <summary>Valor padrão de <see cref="PermissionClaimType"/>.</summary>
    public const string DefaultPermissionClaimType = "tec_perm";

    /// <summary>
    /// Inclui a mensagem e o stack trace das exceções no evento <c>exception</c> das <c>Activity</c>s (traces) de
    /// requisições e notificações. Padrão: <c>false</c> (o evento leva apenas <c>exception.type</c>).
    /// </summary>
    /// <remarks>
    /// <para>Mensagens de exceção (drivers de banco, HTTP, serialização) podem conter dados pessoais, connection strings ou
    /// tokens, e o backend de traces costuma ter acesso mais amplo e retenção diferente dos logs. O log do pipeline
    /// continua recebendo a exceção completa nos dois modos.</para>
    /// <para>Ative apenas se o backend de traces tiver o mesmo controle de acesso dos logs.</para>
    /// </remarks>
    public bool RecordExceptionDetailsInTraces
    {
        get;
        set
        {
            EnsureNotFrozen();
            field = value;
        }
    }

    /// <summary>
    /// Registra handlers, notification handlers, authorizers e validadores (<see cref="IRequestValidator{TRequest}"/>)
    /// encontrados no assembly. Extensões como o <c>AddFluentValidation()</c> também usam os assemblies informados aqui.
    /// </summary>
    /// <remarks>Usa reflexão: em apps com trimming ou Native AOT, prefira o registro explícito.</remarks>
    /// <exception cref="InvalidOperationException">
    /// Tipos do assembly que não carregam, mais de um handler para a mesma requisição ou requisição com marcações
    /// contraditórias.
    /// </exception>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    [RequiresDynamicCode(TypeScanner.DynamicCodeMessage)]
    public CqrsOptions RegisterServicesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureNotFrozen();
        if (_assemblies.Contains(assembly))
            return this;

        RegisterTypes(TypeScanner.GetConcreteTypes(assembly));
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>Registra handlers, notification handlers, authorizers e validadores do assembly que contém <typeparamref name="T"/>.</summary>
    /// <inheritdoc cref="RegisterServicesFromAssembly" path="/remarks"/>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    [RequiresDynamicCode(TypeScanner.DynamicCodeMessage)]
    public CqrsOptions RegisterServicesFromAssemblyContaining<T>() => RegisterServicesFromAssembly(typeof(T).Assembly);

    /// <summary>Registra o handler de um command sem valor de retorno (compatível com Native AOT).</summary>
    /// <example>
    /// <code>
    /// options.AddCommandHandler&lt;InativarClienteCommand, InativarClienteHandler&gt;();
    /// </code>
    /// </example>
    public CqrsOptions AddCommandHandler<TCommand, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TCommand : ICommand
        where THandler : class, IRequestHandler<TCommand, Result> =>
        AddHandler(new RequestHandlerRegistration(typeof(TCommand), typeof(Result),
            ServiceDescriptor.Scoped<IRequestHandler<TCommand, Result>, THandler>(), new RequestHandlerWrapperImpl<TCommand, Result>()));

    /// <summary>Registra o handler de um command que retorna <typeparamref name="TValue"/> (compatível com Native AOT).</summary>
    /// <example>
    /// <code>
    /// options.AddCommandHandler&lt;CriarClienteCommand, Guid, CriarClienteHandler&gt;();
    /// </code>
    /// </example>
    public CqrsOptions AddCommandHandler<TCommand, TValue, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TCommand : ICommand<TValue>
        where THandler : class, IRequestHandler<TCommand, Result<TValue>>
    {
        EnsureNotFrozen();
        ResultFactory.Register<TValue>();
        return AddHandler(new RequestHandlerRegistration(typeof(TCommand), typeof(Result<TValue>),
            ServiceDescriptor.Scoped<IRequestHandler<TCommand, Result<TValue>>, THandler>(),
            new RequestHandlerWrapperImpl<TCommand, Result<TValue>>()));
    }

    /// <summary>Registra o handler de uma query (compatível com Native AOT).</summary>
    /// <example>
    /// <code>
    /// options.AddQueryHandler&lt;ObterClienteQuery, ClienteDto, ObterClienteHandler&gt;();
    /// </code>
    /// </example>
    public CqrsOptions AddQueryHandler<TQuery, TValue, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TQuery : IQuery<TValue>
        where THandler : class, IRequestHandler<TQuery, Result<TValue>>
    {
        EnsureNotFrozen();
        ResultFactory.Register<TValue>();
        return AddHandler(new RequestHandlerRegistration(typeof(TQuery), typeof(Result<TValue>),
            ServiceDescriptor.Scoped<IRequestHandler<TQuery, Result<TValue>>, THandler>(),
            new RequestHandlerWrapperImpl<TQuery, Result<TValue>>()));
    }

    /// <summary>
    /// Registra um handler de notificação (compatível com Native AOT). <typeparamref name="TNotification"/> pode ser um tipo
    /// base ou interface: o handler recebe todas as notificações concretas que o implementam.
    /// </summary>
    /// <remarks>Os handlers rodam na ordem de registro (dentro de cada tipo de notificação).</remarks>
    public CqrsOptions AddNotificationHandler<TNotification, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where TNotification : INotification
        where THandler : class, INotificationHandler<TNotification>
    {
        EnsureNotFrozen();
        _notificationHandlers.Add(new NotificationHandlerRegistration(
            ServiceDescriptor.Scoped<INotificationHandler<TNotification>, THandler>(), NotificationTarget<TNotification>.Instance));
        return this;
    }

    /// <summary>
    /// Registra um authorizer (compatível com Native AOT). <typeparamref name="TRequest"/> pode ser um tipo base ou interface
    /// (que herde <see cref="IBaseRequest"/>): o authorizer vale para todas as requisições que o implementam.
    /// </summary>
    /// <remarks>
    /// Authorizers de tipo base ou interface precisam ser registrados por aqui (ou pela varredura): registrados direto no
    /// container (<c>services.AddScoped&lt;IRequestAuthorizer&lt;T&gt;, ...&gt;()</c>) nunca seriam executados, e o
    /// <c>AddTecCqrs</c> lança <see cref="InvalidOperationException"/>.
    /// </remarks>
    public CqrsOptions AddRequestAuthorizer<TRequest, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAuthorizer>()
        where TRequest : IBaseRequest
        where TAuthorizer : class, IRequestAuthorizer<TRequest>
    {
        EnsureNotFrozen();
        _authorizers.Add(new AuthorizerRegistration(
            ServiceDescriptor.Scoped<IRequestAuthorizer<TRequest>, TAuthorizer>(), AuthorizerTarget<TRequest>.Instance));
        return this;
    }

    /// <summary>
    /// Registra um validador próprio (<see cref="IRequestValidator{TRequest}"/>), sem FluentValidation (compatível com
    /// Native AOT). Para validators do FluentValidation, use o pacote <c>TEC.Cqrs.FluentValidation</c>.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TRequest"/> deve ser o tipo <b>exato</b> de uma requisição com handler registrado: validador de
    /// tipo base ou interface nunca seria executado, e o <c>AddTecCqrs</c> lança <see cref="InvalidOperationException"/>.
    /// </remarks>
    public CqrsOptions AddRequestValidator<TRequest, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>()
        where TRequest : IBaseRequest
        where TValidator : class, IRequestValidator<TRequest>
    {
        EnsureNotFrozen();
        _validators.Add(ServiceDescriptor.Scoped<IRequestValidator<TRequest>, TValidator>());
        return this;
    }

    /// <summary>
    /// Adiciona um behavior próprio ao pipeline, executado depois da autorização e da validação e antes da medição de performance
    /// e da transação. Behaviors próprios rodam na ordem em que foram adicionados.
    /// </summary>
    /// <param name="behaviorType">
    /// Tipo genérico aberto que implementa <see cref="IPipelineBehavior{TRequest, TResponse}"/>
    /// (ex.: <c>typeof(AuditoriaBehavior&lt;,&gt;)</c>).
    /// </param>
    /// <remarks>
    /// O container fecha o tipo genérico para cada requisição. Em Native AOT isso funciona para requisições que são
    /// classes/records (tipos por referência); requisições <c>struct</c> não são suportadas em AOT.
    /// </remarks>
    public CqrsOptions AddBehavior(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.Interfaces)] Type behaviorType)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);
        EnsureNotFrozen();

        bool isValid = behaviorType is { IsGenericTypeDefinition: true, IsAbstract: false, IsInterface: false }
            && behaviorType.GetGenericArguments().Length == 2
            && behaviorType.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        if (!isValid)
        {
            throw new ArgumentException(
                "Informe um tipo genérico aberto (ex.: typeof(MeuBehavior<,>)) que implemente IPipelineBehavior<TRequest, TResponse>.",
                nameof(behaviorType));
        }

        if (!_customBehaviors.Exists(b => b.Type == behaviorType))
            _customBehaviors.Add(new BehaviorRegistration(behaviorType));
        return this;
    }

    /// <summary>
    /// Registra os tipos encontrados na varredura: requisições (para as verificações de inicialização), handlers,
    /// notification handlers, authorizers e validadores.
    /// </summary>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    [RequiresDynamicCode(TypeScanner.DynamicCodeMessage)]
    internal void RegisterTypes(IEnumerable<Type> types)
    {
        EnsureNotFrozen();
        foreach (var type in types)
        {
            if (typeof(IBaseRequest).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false, ContainsGenericParameters: false }
                && !_scannedRequests.Contains(type))
            {
                _scannedRequests.Add(type);
            }

            foreach (var service in TypeScanner.GetClosedInterfaces(type, typeof(IRequestHandler<,>)))
            {
                var arguments = service.GetGenericArguments();
                if (arguments[1].IsGenericType && arguments[1].GetGenericTypeDefinition() == typeof(Result<>))
                    ResultFactory.RegisterDynamic(arguments[1].GetGenericArguments()[0]);

                var wrapper = (RequestHandlerWrapper)Activator.CreateInstance(
                    typeof(RequestHandlerWrapperImpl<,>).MakeGenericType(arguments[0], arguments[1]))!;
                AddHandler(new RequestHandlerRegistration(arguments[0], arguments[1], ServiceDescriptor.Scoped(service, type), wrapper));
            }

            foreach (var service in TypeScanner.GetClosedInterfaces(type, typeof(INotificationHandler<>)))
            {
                var target = (NotificationTarget)Activator.CreateInstance(
                    typeof(NotificationTarget<>).MakeGenericType(service.GetGenericArguments()[0]))!;
                _notificationHandlers.Add(new NotificationHandlerRegistration(ServiceDescriptor.Scoped(service, type), target));
            }

            foreach (var service in TypeScanner.GetClosedInterfaces(type, typeof(IRequestAuthorizer<>)))
            {
                var target = (AuthorizerTarget)Activator.CreateInstance(
                    typeof(AuthorizerTarget<>).MakeGenericType(service.GetGenericArguments()[0]))!;
                _authorizers.Add(new AuthorizerRegistration(ServiceDescriptor.Scoped(service, type), target));
            }

            foreach (var service in TypeScanner.GetClosedInterfaces(type, typeof(IRequestValidator<>)))
                _validators.Add(ServiceDescriptor.Scoped(service, type));
        }
    }

    /// <summary>Valida e adiciona o handler. O mesmo handler registrado de novo é ignorado.</summary>
    private CqrsOptions AddHandler(RequestHandlerRegistration registration)
    {
        EnsureNotFrozen();

        // Falha na inicialização (e não em produção, na primeira requisição)
        RequestMetadata.EnsureValid(registration.RequestType);

        var existing = _handlers.Find(h => h.Descriptor.ServiceType == registration.Descriptor.ServiceType);
        if (existing is null)
        {
            _handlers.Add(registration);
            return this;
        }

        if (existing.Descriptor.ImplementationType == registration.Descriptor.ImplementationType)
            return this;

        throw DuplicateHandler(registration.RequestType, existing.Descriptor, registration.Descriptor);
    }

    internal static InvalidOperationException DuplicateHandler(Type requestType, ServiceDescriptor first, ServiceDescriptor second) => new(
        $"A requisição '{requestType.FullName}' possui mais de um handler: " +
        $"'{first.ImplementationType?.FullName ?? "(factory)"}' e '{second.ImplementationType?.FullName ?? "(factory)"}'. " +
        "Cada requisição deve ter exatamente um handler.");

    /// <summary>Impede novas alterações (chamado ao final do <c>AddTecCqrs</c>).</summary>
    internal void Freeze() => _frozen = true;

    private void EnsureNotFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "As opções do TEC.Cqrs não podem ser alteradas depois do AddTecCqrs. Configure tudo dentro de AddTecCqrs(options => ...).");
        }
    }
}

/// <summary>Behavior próprio, com a anotação de trimming preservada até o registro no container.</summary>
internal sealed class BehaviorRegistration(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type type)
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type Type { get; } = type;
}
