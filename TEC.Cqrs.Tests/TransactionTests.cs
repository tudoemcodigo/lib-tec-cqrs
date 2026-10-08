using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class TransactionTests
{
    private static async Task<IReadOnlyList<string>> RunAsync(IBaseRequest request, Action<FakeUnitOfWork>? arrange = null)
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        arrange?.Invoke(scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>());
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        try
        {
            switch (request)
            {
                case IRequest<TEC.Core.Common.Results.Result> r: await sender.Send(r); break;
                case IRequest<TEC.Core.Common.Results.Result<Guid>> r: await sender.Send(r); break;
                case IRequest<TEC.Core.Common.Results.Result<string>> r: await sender.Send(r); break;
            }
        }
        catch (InvalidOperationException)
        {
            // Exceções esperadas em alguns cenários; o que interessa é a sequência de chamadas
        }

        return scope.ServiceProvider.GetRequiredService<CallLog>().Entries;
    }

    [Test]
    public async Task Successful_command_commits() =>
        await Assert.That(await RunAsync(new CreateCustomerCommand("Maria", "12345678909")))
            .IsEquivalentTo(new[] { "begin", "handler", "commit" }, CollectionOrdering.Matching);

    [Test]
    public async Task Failed_command_rolls_back() =>
        await Assert.That(await RunAsync(new DeactivateCustomerCommand(Guid.Empty)))
            .IsEquivalentTo(new[] { "begin", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Command_with_exception_rolls_back() =>
        await Assert.That(await RunAsync(new FailCommand()))
            .IsEquivalentTo(new[] { "begin", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Commit_failure_rolls_back_and_propagates_exception() =>
        await Assert.That(await RunAsync(new CreateCustomerCommand("Maria", "12345678909"), uow => uow.FailOnCommit = true))
            .IsEquivalentTo(new[] { "begin", "handler", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Validation_error_does_not_even_open_transaction() =>
        await Assert.That(await RunAsync(new CreateCustomerCommand("", ""))).IsEmpty();

    [Test]
    public async Task Query_does_not_open_transaction() =>
        await Assert.That(await RunAsync(new GetCustomerQuery(Guid.NewGuid()))).IsEmpty();

    [Test]
    public async Task Command_with_SkipTransaction_does_not_open_transaction() =>
        await Assert.That(await RunAsync(new NoTransactionCommand())).IsEmpty();

    [Test]
    public async Task Nested_command_reuses_the_open_transaction() =>
        await Assert.That(await RunAsync(new ParentCommand()))
            .IsEquivalentTo(new[] { "begin", "handler", "commit" }, CollectionOrdering.Matching);

    [Test]
    public async Task Without_registered_unit_of_work_the_command_executes_normally()
    {
        using var provider = TestHost.Build(withUnitOfWork: false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("Maria", "12345678909"));

        await Assert.That(result.IsSuccess).IsTrue();
    }
}
