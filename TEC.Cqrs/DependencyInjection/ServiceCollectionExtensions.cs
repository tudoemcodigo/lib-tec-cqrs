using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Behaviors;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.DependencyInjection;

/// <summary>Registro do CQRS no container de injeção de dependência.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o mediator (<see cref="IMediator"/>, <see cref="ISender"/>, <see cref="IPublisher"/>), o pipeline padrão,
    /// as métricas e os handlers, notification handlers, authorizers e validadores configurados em <paramref name="configure"/>
    /// (por varredura de assemblies e/ou registro explícito).
    /// </summary>
    /// <remarks>
    /// <para>Ordem do pipeline: Logging → Exceções → Autorização → Validação → behaviors próprios → Performance → Transação → Handler.</para>
    /// <para>Tudo é registrado como <c>Scoped</c>. Deve ser chamado uma única vez. As opções são congeladas ao final
    /// (alterá-las depois lança <see cref="InvalidOperationException"/>).</para>
    /// <para>O usuário da autorização vem do <see cref="IPrincipalAccessor"/> (em ASP.NET Core, registrado pelo
    /// <c>.AddAspNetCore()</c>); registre a sua implementação para jobs e mensageria. Validators do FluentValidation são
    /// registrados pelo <c>.AddFluentValidation()</c>.</para>
    /// <para>O método é compatível com Native AOT; a varredura (<see cref="CqrsOptions.RegisterServicesFromAssembly"/>) não é.</para>
    /// </remarks>
    /// <returns>Builder para encadear <c>.AddFluentValidation()</c> e <c>.AddAspNetCore()</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Nada registrado, <c>AddTecCqrs</c> chamado mais de uma vez, mais de um handler para a mesma requisição,
    /// requisição sem marcador de command/query ou com marcações contraditórias (command e query;
    /// <c>[AllowAnonymousRequest]</c> e <c>[AuthorizeRequest]</c>), <c>[AuthorizeRequest]</c> com <c>Roles</c>/<c>Policy</c>
    /// em branco, <see cref="IRequestAuthorizer{TRequest}"/> de tipo base ou interface registrado direto no container (nunca
    /// seria executado: use <see cref="CqrsOptions.AddRequestAuthorizer{TRequest, TAuthorizer}"/>),
    /// <see cref="IRequestValidator{TRequest}"/> cujo tipo não é uma requisição concreta conhecida (nunca seria executado) ou
    /// (com <see cref="CqrsOptions.RequireAuthorization"/>, o padrão) requisições sem autorização declarada, listadas na mensagem.
    /// </exception>
    /// <example>
    /// Varredura (JIT):
    /// <code>
    /// builder.Services.AddTecCqrs(options => options
    ///         .RegisterServicesFromAssemblyContaining&lt;Program&gt;()
    ///         .AddBehavior(typeof(AuditoriaBehavior&lt;,&gt;)))
    ///     .AddFluentValidation()
    ///     .AddAspNetCore();
    /// </code>
    /// Registro explícito (Native AOT/trimming):
    /// <code>
    /// builder.Services.AddTecCqrs(options => options
    ///         .AddCommandHandler&lt;CriarClienteCommand, Guid, CriarClienteHandler&gt;()
    ///         .AddQueryHandler&lt;ObterClienteQuery, ClienteDto, ObterClienteHandler&gt;()
    ///         .AddNotificationHandler&lt;ClienteCriadoEvent, EnviarBoasVindasHandler&gt;())
    ///     .AddFluentValidation(fv => fv.AddValidator&lt;CriarClienteCommand, CriarClienteValidator&gt;())
    ///     .AddAspNetCore(http => http.JsonTypeInfoResolver = AppJsonContext.Default);
    /// </code>
    /// </example>
    public static ICqrsBuilder AddTecCqrs(this IServiceCollection services, Action<CqrsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(d => d.ServiceType == typeof(IMediator)))
            throw new InvalidOperationException("AddTecCqrs já foi chamado. Configure tudo em uma única chamada.");

        var options = new CqrsOptions();
        configure(options);

        if (!options.HasRegistrations)
        {
            throw new InvalidOperationException(
                "Nada foi registrado. Informe ao menos um assembly com RegisterServicesFromAssembly(...) ou " +
                "RegisterServicesFromAssemblyContaining<T>(), ou registre os handlers com AddCommandHandler/AddQueryHandler.");
        }

        services.AddSingleton(options);
        services.AddSingleton(new CqrsRegistry(options));
        services.TryAddSingleton(sp => new CqrsMetrics(sp.GetService<IMeterFactory>()));
        services.AddScoped<PipelineContext>();
        services.AddScoped<IMediator, Mediator>();
        services.AddScoped<ISender>(sp => sp.GetRequiredService<IMediator>());
        services.AddScoped<IPublisher>(sp => sp.GetRequiredService<IMediator>());

        // A ordem de registro define a ordem de execução (o primeiro é o mais externo).
        // Autorização antes da validação: quem não tem acesso não recebe detalhes das regras de validação.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ExceptionBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        foreach (var behavior in options.CustomBehaviors)
            services.AddScoped(typeof(IPipelineBehavior<,>), behavior.Type);
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        ApplyRegistrations(services, options);
        EnsureAuthorizersAreApplied(services, options);
        EnsureValidatorsAreApplied(services, options);
        if (options.RequireAuthorization)
            EnsureAuthorizationIsDeclared(services, GetKnownRequests(options));

        options.Freeze();
        return new CqrsBuilder(services, options);
    }

    /// <summary>Adiciona ao container os handlers, notification handlers, authorizers e validadores das opções.</summary>
    internal static void ApplyRegistrations(IServiceCollection services, CqrsOptions options)
    {
        foreach (var handler in options.Handlers)
        {
            var existing = services.FirstOrDefault(d => d.ServiceType == handler.Descriptor.ServiceType);
            if (existing is null)
                services.Add(handler.Descriptor);
            else if (existing.ImplementationType != handler.Descriptor.ImplementationType)
                throw CqrsOptions.DuplicateHandler(handler.RequestType, existing, handler.Descriptor);
        }

        foreach (var registration in options.NotificationHandlers)
            services.TryAddEnumerable(registration.Descriptor);

        foreach (var registration in options.Authorizers)
            services.TryAddEnumerable(registration.Descriptor);

        foreach (var descriptor in options.Validators)
            services.TryAddEnumerable(descriptor);
    }

    /// <summary>Requisições conhecidas: as dos handlers registrados e as encontradas na varredura.</summary>
    internal static IEnumerable<Type> GetKnownRequests(CqrsOptions options) =>
        options.Handlers.Select(h => h.RequestType).Concat(options.ScannedRequests);

    /// <summary>
    /// Falha se houver no container um <see cref="IRequestAuthorizer{TRequest}"/> que o pipeline nunca executaria: registrado
    /// direto no container (fora do <c>AddTecCqrs</c>) para um tipo base ou interface, ou para um tipo que não é uma
    /// requisição conhecida. Em execução só entram os alvos registrados pelo <c>AddTecCqrs</c> e o tipo exato da requisição;
    /// sem esta verificação, o authorizer contaria como autorização declarada e a requisição rodaria sem ele (fail-open).
    /// </summary>
    internal static void EnsureAuthorizersAreApplied(IServiceCollection services, CqrsOptions options)
    {
        var registered = options.Authorizers.Select(a => a.Target.TargetType).ToHashSet();
        var requests = RegisteredServices.GetRequests(services, GetKnownRequests(options));

        foreach (var target in RegisteredServices.GetGenericArguments(services, typeof(IRequestAuthorizer<>))
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            // Direto no container, o authorizer só vale para o tipo exato de uma requisição conhecida
            if (registered.Contains(target)
                || (requests.Contains(target) && !requests.Any(r => r != target && target.IsAssignableFrom(r))))
            {
                continue;
            }

            string authorizer = RegisteredServices.FindImplementationName(services, typeof(IRequestAuthorizer<>), target)
                ?? $"IRequestAuthorizer<{target.Name}>";

            throw new InvalidOperationException(
                $"O authorizer '{authorizer}' de '{target.FullName}' foi registrado direto no container, mas '{target.Name}' " +
                "é um tipo base, uma interface ou não é uma requisição conhecida (com handler registrado): ele nunca seria " +
                "executado pelo pipeline. Registre-o pelo AddTecCqrs, com " +
                $"options.AddRequestAuthorizer<{target.Name}, {authorizer}>() ou pela varredura do assembly.");
        }
    }

    /// <summary>
    /// Falha se houver <see cref="IRequestValidator{TRequest}"/> registrado (pelas opções ou direto no container) para um
    /// tipo que não é uma requisição concreta conhecida (tipo base, interface ou requisição sem handler): o pipeline só
    /// executa validadores do tipo exato, e a requisição rodaria sem a validação esperada. Mesma ideia da verificação de
    /// validators de tipo base/interface do <c>AddFluentValidation</c>.
    /// </summary>
    internal static void EnsureValidatorsAreApplied(IServiceCollection services, CqrsOptions options)
    {
        var requests = RegisteredServices.GetRequests(services, GetKnownRequests(options));

        foreach (var target in RegisteredServices.GetGenericArguments(services, typeof(IRequestValidator<>))
            .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (requests.Contains(target))
                continue;

            string validator = RegisteredServices.FindImplementationName(services, typeof(IRequestValidator<>), target)
                ?? $"IRequestValidator<{target.Name}>";

            throw new InvalidOperationException(
                $"O validador '{validator}' valida '{target.FullName}', que não é uma requisição concreta conhecida (tipo base, " +
                "interface ou requisição sem handler registrado): ele nunca seria executado, pois o pipeline só executa " +
                "validadores do tipo exato da requisição. Crie um IRequestValidator para cada requisição concreta " +
                "(reaproveitando as regras) e registre o handler dela no AddTecCqrs.");
        }
    }

    /// <summary>
    /// Com <see cref="CqrsOptions.RequireAuthorization"/>: falha na inicialização (e não na primeira requisição em produção)
    /// listando as requisições sem <c>[AuthorizeRequest]</c>, <c>[AllowAnonymousRequest]</c> ou <c>IRequestAuthorizer</c>.
    /// </summary>
    internal static void EnsureAuthorizationIsDeclared(IServiceCollection services, IEnumerable<Type> requests)
    {
        var missing = FindRequestsWithoutAuthorization(services, requests);
        if (missing.Count == 0)
            return;

        const int maxListed = 20;
        string list = string.Join(Environment.NewLine, missing.Take(maxListed).Select(t => $"  - {t.FullName}"));
        if (missing.Count > maxListed)
            list += $"{Environment.NewLine}  ... e mais {missing.Count - maxListed}.";

        throw new InvalidOperationException(
            $"{missing.Count} requisição(ões) sem autorização declarada (CqrsOptions.RequireAuthorization está ativo). " +
            "Use [AuthorizeRequest], crie um IRequestAuthorizer (do tipo, de um tipo base ou de uma interface) ou, se a " +
            "requisição for pública, marque com [AllowAnonymousRequest]:" + Environment.NewLine + list);
    }

    /// <summary>Requisições sem autorização declarada, em ordem alfabética, considerando os authorizers já registrados.</summary>
    internal static IReadOnlyList<Type> FindRequestsWithoutAuthorization(IServiceCollection services, IEnumerable<Type> requests)
    {
        var authorizerTargets = RegisteredServices.GetGenericArguments(services, typeof(IRequestAuthorizer<>));

        return [.. requests
            .Distinct()
            .Where(request => !RequestMetadata.DeclaresAuthorization(request, authorizerTargets))
            .OrderBy(request => request.FullName, StringComparer.Ordinal)];
    }
}
