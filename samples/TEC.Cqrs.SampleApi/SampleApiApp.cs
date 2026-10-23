using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Persistence;

namespace TEC.Cqrs.SampleApi;

/// <summary>
/// API de exemplo com os usos típicos do TEC.Cqrs. Alvo do gerador de carga e dos testes de carga e segurança.
/// </summary>
/// <remarks>
/// <para>Configuração (appsettings, variáveis de ambiente ou linha de comando): <c>Exemplo:Clientes</c> (padrão 100),
/// <c>Exemplo:PedidosPorCliente</c> (padrão 20) e <c>Exemplo:MaxPedidosPorCliente</c> (padrão 1000).</para>
/// <para>Os endpoints não usam <c>.RequireAuthorization()</c> de propósito: o exemplo exercita a autorização do pipeline
/// (que protege a requisição em qualquer ponto de entrada). Em uma API real, use as duas camadas.</para>
/// </remarks>
public static class SampleApiApp
{
    public const string CustomerRole = "Cliente";
    public const string AdminRole = "Admin";
    public const string WritePolicy = "PedidosEscrita";

    /// <summary>Tamanho máximo do corpo das requisições (64 KB).</summary>
    public const int MaxRequestBodyBytes = 64 * 1024;

    /// <summary>Cria a aplicação já configurada (sem iniciá-la).</summary>
    /// <param name="args">Argumentos da linha de comando.</param>
    /// <param name="configure">Ajustes extras no builder (ex.: porta em testes).</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes);
        builder.Services.AddAuthentication(SampleAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, SampleAuthentication>(SampleAuthentication.SchemeName, null);
        builder.Services.AddAuthorization(ConfigurePolicies);
        AddOrders(builder.Services, builder.Configuration.GetSection("Exemplo").Get<SampleOptions>() ?? new SampleOptions())
            .AddAspNetCore();

        var app = builder.Build();
        app.UseTecExceptionHandler();
        app.UseAuthentication();
        MapEndpoints(app);
        return app;
    }

    /// <summary>
    /// Registra o domínio de pedidos e o pipeline (sem a integração com ASP.NET Core): usado pela API e, em processo, pelos
    /// testes de carga, que informam o próprio <c>IPrincipalAccessor</c>.
    /// </summary>
    public static ICqrsBuilder AddOrders(IServiceCollection services, SampleOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<OrderStore>();
        services.AddSingleton<SampleMetrics>();
        services.AddScoped<InMemoryUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<InMemoryUnitOfWork>());

        return services
            .AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<OrderStore>())
            .AddFluentValidation();
    }

    /// <summary>Policy de escrita: papel Cliente ou Admin.</summary>
    public static void ConfigurePolicies(AuthorizationOptions options) =>
        options.AddPolicy(WritePolicy, policy => policy.RequireRole(CustomerRole, AdminRole));

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/saude", (ISender sender, CancellationToken ct) => sender.Send(new GetHealthQuery(), ct).ToHttpResult());

        app.MapPost("/pedidos", (CreateOrderCommand command, ISender sender, CancellationToken ct) =>
            sender.Send(command, ct).ToCreatedHttpResult(id => $"/pedidos/{id}"));

        app.MapPost("/pedidos/importacao", (ImportOrdersCommand command, ISender sender, CancellationToken ct) =>
            sender.Send(command, ct).ToHttpResult());

        app.MapGet("/pedidos/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =>
            sender.Send(new GetOrderQuery(id), ct).ToHttpResult());

        app.MapGet("/pedidos", ([FromQuery(Name = "pagina")] int? page, [FromQuery(Name = "tamanho")] int? pageSize, ISender sender, CancellationToken ct) =>
            sender.Send(new ListOrdersQuery(page ?? 1, pageSize ?? 20), ct).ToHttpResult());

        app.MapPost("/pedidos/{id:guid}/estorno", (Guid id, ISender sender, CancellationToken ct) =>
            sender.Send(new RefundOrderCommand(id), ct).ToHttpResult());

        app.MapPost("/diagnostico/falha", (ISender sender, CancellationToken ct) =>
            sender.Send(new SimulateFailureCommand(), ct).ToHttpResult());
    }
}
