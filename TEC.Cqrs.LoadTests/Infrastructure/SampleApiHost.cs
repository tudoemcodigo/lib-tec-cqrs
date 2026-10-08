using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Infrastructure;

/// <summary>
/// API de exemplo hospedada no próprio processo, em Kestrel real (sockets TCP em 127.0.0.1, porta livre escolhida pelo SO).
/// </summary>
public sealed class SampleApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleApiHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Endereço da API (ex.: http://127.0.0.1:53817/).</summary>
    public Uri BaseAddress { get; }

    /// <summary>Contadores do servidor (handlers executados, notificações publicadas).</summary>
    public SampleMetrics Metrics => _app.Services.GetRequiredService<SampleMetrics>();

    /// <summary>"Banco" em memória da API.</summary>
    public OrderStore Store => _app.Services.GetRequiredService<OrderStore>();

    /// <summary>Inicia a API.</summary>
    /// <param name="maxOrdersPerCustomer">Pedidos mantidos por cliente (limita a memória em cargas longas).</param>
    public static async Task<SampleApiHost> StartAsync(int maxOrdersPerCustomer = SampleData.DefaultMaxOrdersPerCustomer)
    {
        var app = SampleApiApp.Create([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration["Exemplo:MaxPedidosPorCliente"] = maxOrdersPerCustomer.ToString(CultureInfo.InvariantCulture);
        });

        await app.StartAsync();
        return new SampleApiHost(app, new Uri(app.Urls.First()));
    }

    /// <summary>Cliente HTTP com uma conexão por worker.</summary>
    public HttpClient CreateClient(int concurrency) =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = concurrency }, disposeHandler: true)
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
