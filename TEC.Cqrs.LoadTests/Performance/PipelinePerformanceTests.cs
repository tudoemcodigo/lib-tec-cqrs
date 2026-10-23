using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.LoadTests.Infrastructure;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Performance;

/// <summary>
/// Custo do pipeline por <c>Send</c>: bytes alocados (métrica determinística, boa para pegar regressões no CI) e vazão.
/// As alocações são medidas na própria thread com handlers que terminam de forma síncrona, então não sofrem
/// interferência de outros testes rodando em paralelo.
/// </summary>
[NotInParallel(LoadSettings.MeasurementKey)]
public class PipelinePerformanceTests
{
    private static readonly ReportFile Reports = new("performance", "Performance do pipeline");

    private const int WarmUp = 2_000;
    private const int Iterations = 20_000;

    private sealed record Measurement(string Scenario, double BytesPerSend, double SendsPerSecond);

    [Test]
    [Category(TestCategories.LoadCi)]
    public async Task AllocationsPerSend_StayWithinBudget()
    {
        await using var orders = OrdersHost.Create();
        await using var workload = Workload.Build();
        var user = OrdersHost.User(SampleData.CustomerId(1));
        var orderId = SampleData.OrderId(1, 0);

        // Orçamentos com folga (cerca do dobro do medido no .NET 8 e no .NET 10: ~3,7 KB, ~4,3 KB, ~8 KB e ~6,3 KB por
        // Send): o objetivo é pegar regressões grosseiras, como reflexão ou alocações por item no caminho quente, não
        // medir a máquina
        (string Scenario, long Budget, Func<Measurement> Measure)[] scenarios =
        [
            ("Query anônima (handler direto no container), mesmo escopo", 8_000,
                () => MeasureInScope(workload, null, "Query anônima", i => new CountQuery(i))),
            ("Query com [AuthorizeRequest] + authorizer do recurso, mesmo escopo", 9_000,
                () => MeasureInScope(orders.Services, user, "Query autorizada", _ => new GetOrderQuery(orderId))),
            ("Command com policy + FluentValidation + transação + evento pós-commit, mesmo escopo", 16_000,
                () => MeasureInScope(orders.Services, user, "Command completo", i => new CreateOrderCommand("Pedido", 10m))),
            ("Query autorizada, escopo por requisição (como no ASP.NET Core)", 13_000,
                () => MeasureScopePerSend(orders.Services, user, _ => new GetOrderQuery(orderId))),
        ];

        var report = new StringBuilder("| Cenário | bytes/Send | orçamento | Sends/s (1 thread) |\n|---|---:|---:|---:|\n");
        var results = new List<(Measurement Result, long Budget)>();
        foreach (var (scenario, budget, measure) in scenarios)
        {
            var result = measure() with { Scenario = scenario };
            results.Add((result, budget));
            report.AppendLine(CultureInfo.InvariantCulture, $"| {scenario} | {result.BytesPerSend:N0} | {budget:N0} | {result.SendsPerSecond:N0} |");
        }
        LoadSettings.Report(Reports, "Alocação por Send", report.ToString());

        foreach (var (result, budget) in results)
            await Assert.That(result.BytesPerSend).IsLessThan(budget).Because(report.ToString());
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    public async Task Throughput_ScalesWithWorkers()
    {
        // Sem trava global no caminho quente: com vários workers a vazão cresce em vez de ficar parada
        await using var host = OrdersHost.Create();
        var duration = LoadSettings.ThroughputDuration;
        int workers = Environment.ProcessorCount;

        double single = await ThroughputAsync(host, 1, duration);
        double parallel = await ThroughputAsync(host, workers, duration);

        LoadSettings.Report(Reports, "Vazão do pipeline (query autorizada, escopo por requisição)", string.Create(CultureInfo.InvariantCulture,
            $"1 worker: {single:N0} Sends/s · {workers} workers: {parallel:N0} Sends/s ({parallel / single:F1}×){Environment.NewLine}"));

        if (workers >= 4)
            await Assert.That(parallel).IsGreaterThan(single * 1.5);
        await Assert.That(single).IsGreaterThan(1_000);
    }

    private static Measurement MeasureInScope<TResponse>(IServiceProvider services, System.Security.Claims.ClaimsPrincipal? user, string name,
        Func<int, IRequest<TResponse>> create)
        where TResponse : Result
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestPrincipalAccessor>().Principal = user;
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return Measure(name, i => SendSync(sender, create(i)));
    }

    private static Measurement MeasureScopePerSend<TResponse>(IServiceProvider services, System.Security.Claims.ClaimsPrincipal user,
        Func<int, IRequest<TResponse>> create)
        where TResponse : Result =>
        Measure("Escopo por requisição", i =>
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TestPrincipalAccessor>().Principal = user;
            SendSync(scope.ServiceProvider.GetRequiredService<ISender>(), create(i));
        });

    private static Measurement Measure(string name, Action<int> send)
    {
        for (int i = 0; i < WarmUp; i++)
            send(i);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Iterations; i++)
            send(i);
        var elapsed = Stopwatch.GetElapsedTime(start);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        return new Measurement(name, (double)allocated / Iterations, Iterations / elapsed.TotalSeconds);
    }

    // Handlers síncronos: o Send termina sem trocar de thread, e a medição por thread captura todas as alocações
    private static void SendSync<TResponse>(ISender sender, IRequest<TResponse> request)
        where TResponse : Result
    {
        var task = sender.Send(request);
        if (!task.IsCompleted)
            throw new InvalidOperationException("O Send não terminou de forma síncrona: a medição de alocação seria parcial.");
        if (task.Result.IsFailure)
            throw new InvalidOperationException($"O Send falhou: {task.Result.Error?.Code}");
    }

    private static async Task<double> ThroughputAsync(OrdersHost host, int workers, TimeSpan duration)
    {
        var user = OrdersHost.User(SampleData.CustomerId(2));
        var request = new GetOrderQuery(SampleData.OrderId(2, 0));
        long sends = 0;
        using var stop = new CancellationTokenSource(duration);

        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await host.SendAsync(request, user);
                Interlocked.Increment(ref sends);
            }
        })));

        return sends / duration.TotalSeconds;
    }
}
