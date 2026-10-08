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
    /// <para>Um <see cref="IPrincipalAccessor"/> registrado antes faz a chamada falhar (a autorização HTTP usaria outro
    /// usuário em silêncio): remova-o, use <see cref="CqrsAspNetCoreOptions.ReplaceExistingPrincipalAccessor"/> para
    /// substituí-lo pelo <c>HttpContext.User</c> ou, para mantê-lo, registre-o depois desta chamada com
    /// <c>services.Replace(...)</c>.</para>
    /// <para>Não substitui um <see cref="IRequestPolicyEvaluator"/> registrado antes. As policies continuam vindo de
    /// <c>services.AddAuthorization(...)</c>.</para>
    /// <para>Compatível com Native AOT. Em apps com trimming/AOT, informe o <see cref="CqrsAspNetCoreOptions.JsonTypeInfoResolver"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// <c>AddAspNetCore</c> chamado mais de uma vez, ou <see cref="IPrincipalAccessor"/> registrado antes sem
    /// <see cref="CqrsAspNetCoreOptions.ReplaceExistingPrincipalAccessor"/>.
    /// </exception>
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
        RegisterPrincipalAccessor(services, options.ReplaceExistingPrincipalAccessor);
        services.TryAddScoped<IRequestPolicyEvaluator, AuthorizationServicePolicyEvaluator>();

        // Sem resolvedor, as respostas mantêm o JsonDefaults.Options do TEC.Core (reflexão), como antes
        if (options.JsonTypeInfoResolver is { } resolver)
            services.AddSingleton(ApiResponseJsonOptions.Create(resolver));

        return builder;
    }

    /// <summary>
    /// Registra o <see cref="HttpContextPrincipalAccessor"/>. Um <see cref="IPrincipalAccessor"/> registrado antes faz a
    /// chamada falhar: mantê-lo em silêncio faria a autorização das requisições HTTP usar outro usuário que não o
    /// <c>HttpContext.User</c> (ex.: a identidade de sistema de um job), e trocá-lo em silêncio quebraria quem o registrou.
    /// </summary>
    private static void RegisterPrincipalAccessor(IServiceCollection services, bool replaceExisting)
    {
        var existing = services.Where(d => d.ServiceType == typeof(IPrincipalAccessor) && !d.IsKeyedService).ToList();
        var foreign = existing.FindLast(d => d.ImplementationType != typeof(HttpContextPrincipalAccessor));
        if (foreign is not null && !replaceExisting)
        {
            string name = foreign.ImplementationType?.Name ?? "registrado por factory/instância";
            throw new InvalidOperationException(
                $"Já existe um IPrincipalAccessor registrado ({name}) antes do AddAspNetCore(). Nas requisições HTTP, a " +
                "autorização usaria esse usuário em vez do HttpContext.User. Para usar o HttpContext.User, remova o registro " +
                "ou chame .AddAspNetCore(http => http.ReplaceExistingPrincipalAccessor = true); para manter o seu accessor " +
                "também nas requisições HTTP, registre-o depois do AddAspNetCore() com services.Replace(...).");
        }

        foreach (var descriptor in existing)
            services.Remove(descriptor);

        services.AddScoped<IPrincipalAccessor, HttpContextPrincipalAccessor>();
    }
}
