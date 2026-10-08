using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.LoadTests.Infrastructure;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Concurrency;

/// <summary>
/// O mediator e o registro (Singleton) atendem muitas requisições ao mesmo tempo, cada uma no seu escopo: os resultados
/// concorrentes precisam ser os mesmos da execução sequencial, sem misturar usuários, transações ou notificações.
/// </summary>
[Category(TestCategories.LoadCi)]
public class ConcurrencyTests
{
    private const int Workers = 64;

    // Registra a primeira divergência (para a mensagem) e conta todas
    private sealed class Divergences
    {
        private readonly ConcurrentQueue<string> _samples = new();
        private int _count;

        public int Count => _count;

        public void Add(string description)
        {
            if (Interlocked.Increment(ref _count) <= 5)
                _samples.Enqueue(description);
        }

        public override string ToString() => string.Join(Environment.NewLine, _samples);
    }

    [Test]
    public async Task ConcurrentUsers_ResourceAuthorization_NeverMixesUsers()
    {
        // Cada requisição tem o próprio usuário: o dono vê o pedido; qualquer outro recebe 404, nunca o pedido alheio
        await using var host = OrdersHost.Create();
        var divergences = new Divergences();

        await Parallel.ForAsync(0, 40_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            int customer = i % SampleData.DefaultCustomers;
            int owner = i % 3 == 0 ? (customer + 1 + i % (SampleData.DefaultCustomers - 1)) % SampleData.DefaultCustomers : customer;
            var orderId = SampleData.OrderId(owner, i % SampleData.DefaultOrdersPerCustomer);

            var result = await host.SendAsync(new GetOrderQuery(orderId), OrdersHost.User(SampleData.CustomerId(customer)), ct);

            if (owner == customer && result.Value?.CustomerId != SampleData.CustomerId(customer))
                divergences.Add($"{SampleData.CustomerId(customer)} não recebeu o próprio pedido {orderId}: {result.Error?.Code}");
            if (owner != customer && (result.IsSuccess || result.Error?.Code != "PEDIDO_NAO_ENCONTRADO"))
                divergences.Add($"{SampleData.CustomerId(customer)} acessou o pedido {orderId} de {SampleData.CustomerId(owner)}");
        });

        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
    }

    [Test]
    public async Task ParallelScopes_Transactions_CommitsMatchAfterCommitNotifications()
    {
        // Sucessos gravam e publicam; falhas de validação e importações com item inválido não gravam nem publicam nada
        await using var host = OrdersHost.Create(maxOrdersPerCustomer: 100_000);
        long expectedCreated = 0;
        var divergences = new Divergences();

        await Parallel.ForAsync(0, 8_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            var user = OrdersHost.User(SampleData.CustomerId(i % SampleData.DefaultCustomers));
            switch (i % 4)
            {
                case 0:
                    var created = await host.SendAsync(new CreateOrderCommand($"Pedido {i}", 10m), user, ct);
                    if (created.IsSuccess) Interlocked.Increment(ref expectedCreated);
                    else divergences.Add($"criar {i}: {created.Error?.Code}");
                    break;
                case 1:
                    var invalid = await host.SendAsync(new CreateOrderCommand("", 0m), user, ct);
                    if (invalid.IsSuccess) divergences.Add($"pedido inválido {i} aceito");
                    break;
                case 2:
                    var imported = await host.SendAsync(new ImportOrdersCommand([.. Enumerable.Range(0, 5).Select(n => new CreateOrderCommand($"Item {n}", 1m))]), user, ct);
                    if (imported.Value == 5) Interlocked.Add(ref expectedCreated, 5);
                    else divergences.Add($"importação {i}: {imported.Error?.Code}");
                    break;
                default:
                    var rolledBack = await host.SendAsync(new ImportOrdersCommand(
                        [new CreateOrderCommand("Item 1", 1m), new CreateOrderCommand("Item 2", 2m), new CreateOrderCommand("", 3m)]), user, ct);
                    if (rolledBack.IsSuccess) divergences.Add($"importação com item inválido {i} aceita");
                    break;
            }
        });

        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
        await Assert.That(host.Store.Created).IsEqualTo(expectedCreated);
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(expectedCreated);
    }

    [Test]
    public async Task Registry_FirstUseRace_ResolvesEveryRequestKindCorrectly()
    {
        // Em cada container novo, 64 threads disputam a primeira chamada: executor criado por reflexão, caches de
        // authorizers e de notificações polimórficas
        for (int round = 0; round < 10; round++)
        {
            await using var provider = Workload.Build();
            using var gate = new Barrier(Workers);
            var divergences = new Divergences();

            await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Factory.StartNew(async () =>
            {
                gate.SignalAndWait();
                await using var scope = provider.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TestPrincipalAccessor>().Principal = OrdersHost.User($"u{worker}");
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                switch (worker % 3)
                {
                    case 0:
                        var count = await sender.Send(new CountQuery(worker));
                        if (count.Value != worker + 1) divergences.Add($"ContarQuery({worker}) = {count.Value}");
                        break;
                    case 1:
                        var own = await sender.Send(new ReadResourceQuery($"u{worker}"));
                        var other = await sender.Send(new ReadResourceQuery($"u{worker + 1}"));
                        if (own.Value != $"u{worker}:u{worker}" || other.IsSuccess) divergences.Add($"LerRecurso de u{worker}: {own.Value} / {other.IsSuccess}");
                        break;
                    default:
                        var batch = await sender.Send(new ProcessBatchCommand(InnerCommands: 3, Notifications: 2));
                        if (batch.IsFailure) divergences.Add($"lote de {worker}: {batch.Error?.Code}");
                        break;
                }
            }, TaskCreationOptions.LongRunning).Unwrap()));

            var counters = provider.GetRequiredService<WorkloadCounters>();
            int batches = Enumerable.Range(0, Workers).Count(w => w % 3 == 2);
            await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
            await Assert.That(counters.CommittedSteps).IsEqualTo(batches * 3L);
            // Cada notificação chega ao handler do tipo concreto e ao da interface, uma vez cada
            await Assert.That(counters.Notifications).IsEqualTo(batches * 2L);
            await Assert.That(counters.InterfaceNotifications).IsEqualTo(batches * 2L);
        }
    }

    [Test]
    public async Task ParallelScopes_FailedBatches_RollBackEverything()
    {
        // Lotes que falham no fim desfazem todos os commands internos e descartam as notificações
        await using var provider = Workload.Build();
        var counters = provider.GetRequiredService<WorkloadCounters>();

        var results = new ConcurrentBag<bool>();
        await Parallel.ForAsync(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            await using var scope = provider.CreateAsyncScope();
            var result = await scope.ServiceProvider.GetRequiredService<ISender>()
                .Send(new ProcessBatchCommand(InnerCommands: 10, Notifications: 3, Fail: i % 2 == 1), ct);
            results.Add(result.IsSuccess);
        });

        int succeeded = results.Count(ok => ok);
        await Assert.That(succeeded).IsEqualTo(1_000);
        await Assert.That(counters.CommittedSteps).IsEqualTo(succeeded * 10L);
        await Assert.That(counters.Notifications).IsEqualTo(succeeded * 3L);
    }

    [Test]
    public async Task SameScope_ParallelQueries_DoNotMixFrames()
    {
        // Sends em paralelo no mesmo escopo (permitido para queries): cada fluxo assíncrono tem a própria pilha
        await using var provider = Workload.Build();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestPrincipalAccessor>().Principal = OrdersHost.User("u1");
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var results = await Task.WhenAll(Enumerable.Range(0, 5_000).Select(async i =>
        {
            await Task.Yield();
            return (i, (await sender.Send(new CountQuery(i))).Value);
        }));

        await Assert.That(results.All(r => r.Value == r.i + 1)).IsTrue();
    }

    [Test]
    public async Task ExceptionsUnderConcurrency_DoNotLeakAcrossScopes()
    {
        // Exceções (500) em metade das requisições não afetam as outras nem deixam transação aberta
        await using var host = OrdersHost.Create(maxOrdersPerCustomer: 100_000);
        long created = 0, exceptions = 0;

        await Parallel.ForAsync(0, 4_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            var user = OrdersHost.User(SampleData.CustomerId(i % SampleData.DefaultCustomers));
            if (i % 2 == 0)
            {
                if ((await host.SendAsync(new CreateOrderCommand($"Pedido {i}", 5m), user, ct)).IsSuccess)
                    Interlocked.Increment(ref created);
                return;
            }

            try
            {
                await host.SendAsync(new SimulateFailureCommand(), user, ct);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref exceptions);
            }
        });

        await Assert.That(created).IsEqualTo(2_000);
        await Assert.That(exceptions).IsEqualTo(2_000);
        await Assert.That(host.Store.Created).IsEqualTo(2_000);
        await Assert.That(host.Metrics.OrderCreatedEvents).IsEqualTo(2_000);
    }
}
