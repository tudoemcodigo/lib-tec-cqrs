using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.LoadTests.Infrastructure;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Volume;

/// <summary>Grandes volumes: milhões de requisições sem crescimento de memória e transações/lotes muito grandes.</summary>
[Explicit]
[Category(TestCategories.LoadHeavy)]
[NotInParallel(LoadSettings.MeasurementKey)]
public class VolumeTests
{
    private static readonly ReportFile Reports = new("volume", "Volume");

    [Test]
    public async Task MillionsOfSends_ScopePerRequest_RetainedMemoryStaysConstant()
    {
        // Só operações que não gravam (consultas, negações, validação, rollback, exceção): o que crescer é vazamento
        // do pipeline (caches por requisição, frames, notificações pendentes, escopos)
        await using var host = OrdersHost.Create();
        int total = LoadSettings.Sends;
        int checkpoint = Math.Max(total / 10, 1);
        long baseline = 0;
        var samples = new List<long>();
        long done = 0;
        var watch = Stopwatch.StartNew();

        await Parallel.ForAsync(0, total, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (i, ct) =>
        {
            int customer = i % SampleData.DefaultCustomers;
            var user = OrdersHost.User(SampleData.CustomerId(customer));
            switch (i % 8)
            {
                case < 4:
                    await host.SendAsync(new GetOrderQuery(SampleData.OrderId(customer, i % SampleData.DefaultOrdersPerCustomer)), user, ct);
                    break;
                case 4:
                    await host.SendAsync(new GetOrderQuery(SampleData.OrderId((customer + 1) % SampleData.DefaultCustomers, 0)), user, ct);
                    break;
                case 5:
                    await host.SendAsync(new CreateOrderCommand("", 0m), user, ct);
                    break;
                case 6:
                    await host.SendAsync(new ImportOrdersCommand([new CreateOrderCommand("Item", 1m), new CreateOrderCommand("", 1m)]), user, ct);
                    break;
                default:
                    try { await host.SendAsync(new SimulateFailureCommand(), user, ct); }
                    catch (InvalidOperationException) { }
                    break;
            }

            if (Interlocked.Increment(ref done) % checkpoint == 0)
            {
                lock (samples)
                    samples.Add(MemoryProbe.RetainedBytes());
            }
        });
        watch.Stop();

        // Linha de base depois do aquecimento (2º ponto): JIT, caches por tipo e pools já preenchidos
        baseline = samples[1];
        long last = samples[^1];
        LoadSettings.Report(Reports, "Milhões de Sends (escopo por requisição)", string.Create(CultureInfo.InvariantCulture,
            $"Sends: {total:N0} · {watch.Elapsed.TotalSeconds:F1} s ({total / watch.Elapsed.TotalSeconds:N0}/s) · memória retida: {string.Join(" → ", samples.Select(MemoryProbe.Megabytes))}{Environment.NewLine}"));

        await Assert.That(host.Store.Created).IsEqualTo(0); // nada foi gravado
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(0);
        await Assert.That(last - baseline).IsLessThan(16L * 1024 * 1024);
    }

    [Test]
    public async Task NestedCommands_LongSequenceInOneTransaction_ScalesLinearly()
    {
        // Cada command interno verifica a transação do externo: o custo por command não pode crescer com a quantidade
        await using var provider = Workload.Build();
        int n = LoadSettings.NestedCommands;

        // Melhor de 3 de cada tamanho: descarta pausas de GC e ruído da máquina
        var half = TimeSpan.MaxValue;
        var full = TimeSpan.MaxValue;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            half = Min(half, await TimeBatchAsync(provider, n / 2));
            full = Min(full, await TimeBatchAsync(provider, n));
        }

        LoadSettings.Report(Reports, "Commands internos em uma transação", string.Create(CultureInfo.InvariantCulture,
            $"{n / 2:N0} internos: {half.TotalMilliseconds:F0} ms · {n:N0} internos: {full.TotalMilliseconds:F0} ms ({full / half:F1}×, melhor de 3){Environment.NewLine}"));

        await Assert.That(provider.GetRequiredService<WorkloadCounters>().CommittedSteps).IsEqualTo(3 * (n / 2 + (long)n));
        // Linear: o dobro de commands leva perto do dobro do tempo (quadrático levaria 4×)
        await Assert.That(full / half).IsLessThan(3.0);
    }

    [Test]
    public async Task AfterCommit_LargeNotificationBatch_AllDeliveredAndReleased()
    {
        await using var provider = Workload.Build();
        int n = LoadSettings.Notifications;
        long before = MemoryProbe.RetainedBytes();

        var watch = Stopwatch.StartNew();
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ProcessBatchCommand(InnerCommands: 1, Notifications: n));
            await Assert.That(result.IsSuccess).IsTrue();
        }
        watch.Stop();
        long after = MemoryProbe.RetainedBytes();

        var counters = provider.GetRequiredService<WorkloadCounters>();
        LoadSettings.Report(Reports, "Lote de notificações pós-commit", string.Create(CultureInfo.InvariantCulture,
            $"Notificações: {n:N0} (2 handlers cada) · {watch.Elapsed.TotalMilliseconds:F0} ms · memória retida: {MemoryProbe.Megabytes(before)} → {MemoryProbe.Megabytes(after)}{Environment.NewLine}"));

        await Assert.That(counters.Notifications).IsEqualTo(n);
        await Assert.That(counters.InterfaceNotifications).IsEqualTo(n);
        await Assert.That(after - before).IsLessThan(8L * 1024 * 1024); // a fila pendente foi liberada
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(60));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static async Task<TimeSpan> TimeBatchAsync(IServiceProvider provider, int innerCount)
    {
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var watch = Stopwatch.StartNew();
        var result = await sender.Send(new ProcessBatchCommand(innerCount, Notifications: 0));
        watch.Stop();
        if (result.IsFailure)
            throw new InvalidOperationException($"Lote falhou: {result.Error?.Code}");
        return watch.Elapsed;
    }
}
