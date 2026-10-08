using TEC.Cqrs.LoadGenerator;
using TEC.Cqrs.LoadTests.Infrastructure;

namespace TEC.Cqrs.LoadTests.Api;

/// <summary>
/// Carga HTTP na API de exemplo hospedada em processo (Kestrel real em 127.0.0.1), com o gerador TEC.Cqrs.LoadGenerator.
/// Cada cenário de falha (validação, acesso alheio, sem autenticação, sem papel) espera o próprio status: uma resposta
/// diferente conta como erro.
/// </summary>
/// <remarks>
/// Para medir uma API publicada em outro servidor, use o gerador pela linha de comando (veja samples/README.md).
/// </remarks>
[NotInParallel(LoadSettings.MeasurementKey)]
public class SampleApiLoadTests
{
    private static readonly ReportFile Reports = new("api", "Carga HTTP na API de exemplo");

    private const string AllScenarios =
        "saude,obter,listar,criar,importar,importar-invalido,erro-validacao,acesso-alheio,nao-autenticado,sem-papel,estorno-negado,estorno,erro-interno:1";

    [Test]
    [Category(TestCategories.LoadCi)]
    // Sozinho no processo: a chave MeasurementKey não cobre os ConcurrencyTests e SecurityLoadTests (Parallel.ForAsync), que
    // saturam o thread pool; no runner de 2 CPUs do CI nenhuma requisição terminava dentro da janela medida
    [NotInParallel]
    public async Task SmokeLoad_AllScenarios_WithoutErrors()
    {
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(16);

        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = 16,
            WarmUp = TimeSpan.FromSeconds(1),
            Duration = TimeSpan.FromSeconds(3),
            Scenarios = SampleScenarios.Parse(AllScenarios),
        });
        LoadSettings.Report(Reports, "API de exemplo — fumaça (CI)", report.ToText());

        await Assert.That(report.Requests).IsGreaterThan(100);
        await Assert.That(report.Errors).IsEqualTo(0).Because(report.ToText());
        await Assert.That(report.Scenarios.All(s => s.Requests > 0)).IsTrue().Because("todos os cenários devem ser exercitados");
        // Pós-commit sob carga: cada pedido confirmado publicou exatamente um evento; importações desfeitas, nenhum
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(host.Store.Created);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    public async Task SustainedLoad_ErrorRateAndLatencyWithinLimits()
    {
        await using var host = await SampleApiHost.StartAsync(maxOrdersPerCustomer: 100);
        int concurrency = LoadSettings.ApiConcurrency;
        using var client = host.CreateClient(concurrency);

        long memoryBefore = MemoryProbe.RetainedBytes();
        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = concurrency,
            WarmUp = TimeSpan.FromSeconds(Math.Clamp(LoadSettings.ApiDuration.TotalSeconds / 6, 1, 10)),
            Duration = LoadSettings.ApiDuration,
            Scenarios = SampleScenarios.Parse(AllScenarios),
        });
        long memoryAfter = MemoryProbe.RetainedBytes();

        LoadSettings.Report(Reports, "API de exemplo — carga sustentada",
            report.ToText() + $"Memória retida (cliente + servidor): {MemoryProbe.Megabytes(memoryBefore)} → {MemoryProbe.Megabytes(memoryAfter)}{Environment.NewLine}");

        // Limites generosos: o objetivo é pegar regressões grosseiras (erros, travamentos, vazamento), não medir a máquina
        await Assert.That(report.ErrorRate).IsLessThanOrEqualTo(0.001).Because(report.ToText());
        await Assert.That(report.Latency.P99).IsLessThan(2_000);
        await Assert.That(memoryAfter - memoryBefore).IsLessThan(128L * 1024 * 1024);
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(host.Store.Created);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    public async Task WriteHeavyLoad_TransactionsAndNestedCommands_StayConsistent()
    {
        // Só escrita: commands com transação, importações (commands aninhados) e importações desfeitas
        await using var host = await SampleApiHost.StartAsync(maxOrdersPerCustomer: 100);
        using var client = host.CreateClient(Environment.ProcessorCount * 4);

        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = Environment.ProcessorCount * 4,
            WarmUp = TimeSpan.FromSeconds(Math.Clamp(LoadSettings.WriteDuration.TotalSeconds / 10, 1, 3)),
            Duration = LoadSettings.WriteDuration,
            Scenarios = SampleScenarios.Parse("criar:5,importar:3,importar-invalido:2"),
        });
        LoadSettings.Report(Reports, "API de exemplo — escrita com transações e commands aninhados", report.ToText());

        await Assert.That(report.Errors).IsEqualTo(0).Because(report.ToText());
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(host.Store.Created);
    }
}
