using System.Diagnostics;
using System.Globalization;
using System.Text;
using TEC.Cqrs.LoadTests.Infrastructure;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Soak;

/// <summary>
/// Soak: carga mista contínua por minutos no pipeline completo, verificando que memória, handles e vazão ficam estáveis
/// (sem vazamentos nem degradação). Duração: TEC_CARGA_SOAK_SEGUNDOS (padrão 120 s) × TEC_CARGA_FATOR.
/// </summary>
[Explicit]
[Category(TestCategories.LoadHeavy)]
[NotInParallel(LoadSettings.MeasurementKey)]
public class SoakTests
{
    private static readonly ReportFile Reports = new("soak", "Soak (longa duração)");

    private sealed record Sample(double Seconds, long Operations, long RetainedBytes, int Handles);

    [Test]
    public async Task MixedWorkload_MemoryHandlesAndThroughputStayStable()
    {
        var duration = LoadSettings.SoakDuration;
        // Cerca de 24 amostras em qualquer duração (o fator pode encurtar o soak para segundos)
        var interval = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds / 24.0, 0.25, 30));

        // Poucos pedidos por cliente: o "banco" chega ao limite logo no aquecimento e para de crescer
        await using var host = OrdersHost.Create(maxOrdersPerCustomer: 40);
        var admin = OrdersHost.User(SampleData.Admin, SampleApiApp.AdminRole);

        long operations = 0;
        using var stop = new CancellationTokenSource(duration);

        async Task WorkerAsync(int id)
        {
            var random = new Random(id);
            while (!stop.IsCancellationRequested)
            {
                int customer = random.Next(SampleData.DefaultCustomers);
                var user = OrdersHost.User(SampleData.CustomerId(customer));
                switch (random.Next(100))
                {
                    case < 35:
                        await host.SendAsync(new GetOrderQuery(SampleData.OrderId(customer, random.Next(SampleData.DefaultOrdersPerCustomer))), user);
                        break;
                    case < 50:
                        await host.SendAsync(new ListOrdersQuery(1, 10), user);
                        break;
                    case < 65:
                        await host.SendAsync(new CreateOrderCommand($"Pedido {random.Next()}", random.Next(1, 1_000)), user);
                        break;
                    case < 72:
                        await host.SendAsync(new ImportOrdersCommand([.. Enumerable.Range(0, 5).Select(i => new CreateOrderCommand($"Item {i}", 1m))]), user);
                        break;
                    case < 80:
                        await host.SendAsync(new ImportOrdersCommand([new CreateOrderCommand("Item", 1m), new CreateOrderCommand("", 1m)]), user);
                        break;
                    case < 88:
                        await host.SendAsync(new GetOrderQuery(SampleData.OrderId((customer + 1) % SampleData.DefaultCustomers, 0)), user);
                        break;
                    case < 93:
                        await host.SendAsync(new CreateOrderCommand("", -1m), user);
                        break;
                    case < 96:
                        await host.SendAsync(new RefundOrderCommand(SampleData.OrderId(customer, 0)), random.Next(2) == 0 ? admin : user);
                        break;
                    default:
                        try { await host.SendAsync(new SimulateFailureCommand(), user); }
                        catch (InvalidOperationException) { }
                        break;
                }

                Interlocked.Increment(ref operations);
            }
        }

        var watch = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(i => Task.Run(() => WorkerAsync(i))).ToArray();
        var samples = new List<Sample>();
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stop.Token); }
            catch (OperationCanceledException) { break; }
            samples.Add(new Sample(watch.Elapsed.TotalSeconds, Interlocked.Read(ref operations), MemoryProbe.RetainedBytes(), MemoryProbe.HandleCount()));
        }
        await Task.WhenAll(workers);

        await Assert.That(samples.Count).IsGreaterThanOrEqualTo(8).Because("o soak precisa de amostras suficientes (aumente TEC_CARGA_SOAK_SEGUNDOS ou TEC_CARGA_FATOR)");

        // Vazão de cada intervalo entre amostras
        var rates = samples.Select((s, i) => i == 0
            ? s.Operations / s.Seconds
            : (s.Operations - samples[i - 1].Operations) / (s.Seconds - samples[i - 1].Seconds)).ToArray();

        // Descarta o primeiro quarto (aquecimento: JIT, pools e caches) e compara o início com o fim da janela estável
        int skip = samples.Count / 4;
        int third = Math.Max((samples.Count - skip) / 3, 1);
        var first = samples.Skip(skip).Take(third).ToList();
        var last = samples.TakeLast(third).ToList();
        double firstThroughput = rates.Skip(skip).Take(third).Average();
        double lastThroughput = rates.TakeLast(third).Average();
        long firstMemory = Median(first.Select(s => s.RetainedBytes));
        long lastMemory = Median(last.Select(s => s.RetainedBytes));

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Duração: {duration.TotalSeconds:F0} s · workers: {Environment.ProcessorCount} · operações: {operations:N0}");
        report.AppendLine("| t (s) | operações | memória retida | handles |");
        report.AppendLine("|---:|---:|---:|---:|");
        foreach (var s in samples)
            report.AppendLine(CultureInfo.InvariantCulture, $"| {s.Seconds:F1} | {s.Operations:N0} | {MemoryProbe.Megabytes(s.RetainedBytes)} | {s.Handles} |");
        report.AppendLine(CultureInfo.InvariantCulture, $"Vazão: {firstThroughput:N0} → {lastThroughput:N0} op/s · memória (mediana): {MemoryProbe.Megabytes(firstMemory)} → {MemoryProbe.Megabytes(lastMemory)}");
        LoadSettings.Report(Reports, "Soak em processo (carga mista no pipeline)", report.ToString());

        // Consistência depois de minutos de carga: cada pedido confirmado publicou exatamente um evento
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(host.Store.Created);
        await Assert.That(lastMemory - firstMemory).IsLessThan(Math.Max(16L * 1024 * 1024, firstMemory / 5)).Because(report.ToString());
        await Assert.That(last[^1].Handles - first[0].Handles).IsLessThan(200).Because(report.ToString());
        await Assert.That(lastThroughput).IsGreaterThan(firstThroughput * 0.6).Because(report.ToString());
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
