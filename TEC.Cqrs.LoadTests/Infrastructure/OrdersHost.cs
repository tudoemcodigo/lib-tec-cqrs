using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Infrastructure;

/// <summary>
/// O domínio da API de exemplo (pedidos) com o pipeline completo, em processo e sem HTTP: mede o TEC.Cqrs isolado do
/// Kestrel. O usuário de cada requisição é informado por <see cref="SendAsync{TResponse}"/>.
/// </summary>
public sealed class OrdersHost : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<(string, string), ClaimsPrincipal> Principals = new();

    private OrdersHost(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    public OrderStore Store => Services.GetRequiredService<OrderStore>();

    public SampleMetrics Metrics => Services.GetRequiredService<SampleMetrics>();

    /// <param name="maxOrdersPerCustomer">Pedidos mantidos por cliente (limita a memória em cargas longas).</param>
    public static OrdersHost Create(int maxOrdersPerCustomer = SampleData.DefaultMaxOrdersPerCustomer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<TestPrincipalAccessor>();
        services.AddScoped<IPrincipalAccessor>(sp => sp.GetRequiredService<TestPrincipalAccessor>());
        services.AddAuthorizationCore(SampleApiApp.ConfigurePolicies);
        services.AddScoped<IRequestPolicyEvaluator, PolicyEvaluator>();
        SampleApiApp.AddOrders(services, new SampleOptions { MaxOrdersPerCustomer = maxOrdersPerCustomer });

        return new OrdersHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
    }

    /// <summary>Usuário autenticado com os papéis informados (instância reaproveitada).</summary>
    public static ClaimsPrincipal User(string id, string roles = SampleApiApp.CustomerRole) =>
        Principals.GetOrAdd((id, roles), static key =>
        {
            List<Claim> claims = [new(ClaimTypes.Name, key.Item1), new(SampleAuthentication.CustomerClaim, key.Item1)];
            claims.AddRange(key.Item2.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim(ClaimTypes.Role, p)));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Teste", ClaimTypes.Name, ClaimTypes.Role));
        });

    /// <summary>Envia a requisição em um escopo novo (como uma requisição HTTP), como <paramref name="user"/>.</summary>
    public async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, ClaimsPrincipal? user, CancellationToken cancellationToken = default)
        where TResponse : Result
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestPrincipalAccessor>().Principal = user;
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, cancellationToken);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    private sealed class PolicyEvaluator(IAuthorizationService authorization) : IRequestPolicyEvaluator
    {
        public async Task<bool> AuthorizeAsync(ClaimsPrincipal user, object request, string policy, CancellationToken cancellationToken) =>
            (await authorization.AuthorizeAsync(user, request, policy)).Succeeded;
    }
}

/// <summary>Usuário da requisição em processo (Scoped): definido antes do <c>Send</c>.</summary>
public sealed class TestPrincipalAccessor : IPrincipalAccessor
{
    public ClaimsPrincipal? Principal { get; set; }
}
