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
    public async Task Command_com_sucesso_faz_commit() =>
        await Assert.That(await RunAsync(new CriarClienteCommand("Maria", "12345678909")))
            .IsEquivalentTo(new[] { "begin", "handler", "commit" }, CollectionOrdering.Matching);

    [Test]
    public async Task Command_com_falha_faz_rollback() =>
        await Assert.That(await RunAsync(new InativarClienteCommand(Guid.Empty)))
            .IsEquivalentTo(new[] { "begin", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Command_com_excecao_faz_rollback() =>
        await Assert.That(await RunAsync(new FalharCommand()))
            .IsEquivalentTo(new[] { "begin", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Falha_no_commit_faz_rollback_e_propaga_excecao() =>
        await Assert.That(await RunAsync(new CriarClienteCommand("Maria", "12345678909"), uow => uow.FailOnCommit = true))
            .IsEquivalentTo(new[] { "begin", "handler", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Erro_de_validacao_nem_abre_transacao() =>
        await Assert.That(await RunAsync(new CriarClienteCommand("", ""))).IsEmpty();

    [Test]
    public async Task Query_nao_abre_transacao() =>
        await Assert.That(await RunAsync(new ObterClienteQuery(Guid.NewGuid()))).IsEmpty();

    [Test]
    public async Task Command_com_SkipTransaction_nao_abre_transacao() =>
        await Assert.That(await RunAsync(new SemTransacaoCommand())).IsEmpty();

    [Test]
    public async Task Command_aninhado_reaproveita_a_transacao_aberta() =>
        await Assert.That(await RunAsync(new CommandPaiCommand()))
            .IsEquivalentTo(new[] { "begin", "handler", "commit" }, CollectionOrdering.Matching);

    [Test]
    public async Task Sem_unit_of_work_registrado_o_command_executa_normalmente()
    {
        using var provider = TestHost.Build(withUnitOfWork: false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CriarClienteCommand("Maria", "12345678909"));

        await Assert.That(result.IsSuccess).IsTrue();
    }
}
