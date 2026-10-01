using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.AspNetCore.Internal;
using TEC.Cqrs.Authorization;

namespace TEC.Cqrs.DependencyInjection;

/// <summary>Integração do pipeline do TEC.Cqrs com o ASP.NET Core.</summary>
public static class CqrsBuilderAspNetCoreExtensions
{
    /// <summary>
    /// Integra o pipeline ao ASP.NET Core: o usuário da autorização passa a ser o <c>HttpContext.User</c>
    /// (<see cref="IPrincipalAccessor"/>), as policies de <c>[AuthorizeRequest(Policy = ...)]</c> são avaliadas pelo
    /// <c>IAuthorizationService</c> (<see cref="IRequestPolicyEvaluator"/>) e as respostas HTTP usam as opções JSON
    /// informadas (<see cref="CqrsAspNetCoreOptions.JsonTypeInfoResolver"/>).
    /// </summary>
    /// <remarks>
    /// <para>Não substitui um <see cref="IPrincipalAccessor"/> ou <see cref="IRequestPolicyEvaluator"/> registrado antes.
    /// As policies continuam vindo de <c>services.AddAuthorization(...)</c>.</para>
    /// <para>Compatível com Native AOT. Em apps com trimming/AOT, informe o <see cref="CqrsAspNetCoreOptions.JsonTypeInfoResolver"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>AddAspNetCore</c> chamado mais de uma vez.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddTecCqrs(options => options.RegisterServicesFromAssemblyContaining&lt;Program&gt;())
    ///     .AddFluentValidation()
    ///     .AddAspNetCore();
    /// builder.Services.AddAuthorization(o => o.AddPolicy("ClientesEscrita", p => p.RequireRole("Admin")));
    /// </code>
    /// </example>
    public static ICqrsBuilder AddAspNetCore(this ICqrsBuilder builder, Action<CqrsAspNetCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(CqrsAspNetCoreOptions)))
            throw new InvalidOperationException("AddAspNetCore já foi chamado. Configure tudo em uma única chamada.");

        var options = new CqrsAspNetCoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddHttpContextAccessor();
        services.TryAddScoped<IPrincipalAccessor, HttpContextPrincipalAccessor>();
        services.TryAddScoped<IRequestPolicyEvaluator, AuthorizationServicePolicyEvaluator>();

        // Sem resolvedor, as respostas mantêm o JsonDefaults.Options do TEC.Core (reflexão), como antes
        if (options.JsonTypeInfoResolver is { } resolver)
            services.AddSingleton(ApiResponseJsonOptions.Create(resolver));

        return builder;
    }
}
