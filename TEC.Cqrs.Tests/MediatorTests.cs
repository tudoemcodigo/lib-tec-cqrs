using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class MediatorTests
{
    [Test]
    public async Task Send_command_retorna_valor_do_handler()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.Send(new CriarClienteCommand("Maria", "12345678909"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(CriarClienteHandler.CreatedId);
    }

    [Test]
    public async Task Send_query_retorna_valor_do_handler()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ObterClienteQuery(Guid.NewGuid()));

        await Assert.That(result.Value).IsEqualTo("Maria");
    }

    [Test]
    public async Task Validacao_com_erro_interrompe_o_pipeline_sem_chamar_o_handler()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CriarClienteCommand("", "123"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Errors.Select(e => e.Type))
            .IsEquivalentTo(new[] { ErrorType.Validation, ErrorType.Validation }, CollectionOrdering.Matching);
        await Assert.That(result.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "NOME_OBRIGATORIO", "CPF_INVALIDO" }, CollectionOrdering.Matching);
        await Assert.That(result.Errors.Select(e => e.Field))
            .IsEquivalentTo(new string?[] { "nome", "cpf" }, CollectionOrdering.Matching);
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain("handler");
    }

    [Test]
    public async Task Validacao_mantem_nome_original_do_campo_quando_camelCase_desativado()
    {
        using var provider = TestHost.Build(validation: o => o.CamelCaseValidationFields = false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CriarClienteCommand("", "12345678909"));

        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0].Field).IsEqualTo("Nome");
    }

    [Test]
    public async Task Command_sem_valor_propaga_falha_do_handler()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new InativarClienteCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("CLIENTE_JA_INATIVO");
        await Assert.That(result.Error.Type).IsEqualTo(ErrorType.BusinessRule);
    }

    [Test]
    public async Task AppException_lancada_no_handler_vira_Result_de_falha()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ObterClienteQuery(Guid.Empty));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error!.Code).IsEqualTo("CLIENTE_NAO_ENCONTRADO");
        await Assert.That(result.Error.Type).IsEqualTo(ErrorType.NotFound);
    }

    [Test]
    public async Task IntegrationException_vira_falha_de_servico_externo()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new IntegrarCommand());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.ExternalService);
    }

    [Test]
    public async Task Excecao_nao_tratada_e_propagada()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FalharCommand()); })
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Requisicao_sem_handler_lanca_excecao_clara()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SemHandlerQuery()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(SemHandlerQuery));
    }

    [Test]
    public async Task Behavior_proprio_envolve_o_handler()
    {
        using var provider = TestHost.Build(o => o.AddBehavior(typeof(RegistroBehavior<,>)));
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CriarClienteCommand("Maria", "12345678909"));

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "behavior:antes", "handler", "behavior:depois" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Publish_executa_todos_os_handlers_em_ordem()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new ClienteCriadoEvent(Guid.NewGuid()));

        // Ordem determinística: nome completo do handler (AtualizarCrmHandler < EnviarEmailHandler)
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "crm", "email" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Publish_usa_o_tipo_real_da_notificacao()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        INotification notification = new ClienteCriadoEvent(Guid.NewGuid());

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(notification);

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Publish_sem_handlers_nao_falha()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await Assert.That(() => scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new SemHandlersEvent()))
            .ThrowsNothing();
    }

    private sealed record SemHandlersEvent : INotification;
}

public class RegistrationTests
{
    [Test]
    public async Task AddTecCqrs_sem_assembly_lanca_excecao()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddTecCqrs(_ => { })).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Handler_registrado_direto_no_container_com_outro_handler_falha_no_AddTecCqrs()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestHandler<InativarClienteCommand, Result>, HandlerDuplicado<int>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(InativarClienteCommand));
    }

    [Test]
    public async Task AddTecCqrs_chamado_duas_vezes_lanca_excecao()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>());

        await Assert.That(() => services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Dois_handlers_para_a_mesma_requisicao_falham_no_registro()
    {
        var options = new CqrsOptions();

        var ex = await Assert.That(() => options.RegisterTypes([typeof(InativarClienteHandler), typeof(HandlerDuplicado<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(InativarClienteCommand));
    }

    [Test]
    public async Task Mesmo_handler_registrado_duas_vezes_e_ignorado()
    {
        var services = new ServiceCollection();
        var options = new CqrsOptions();

        options.RegisterTypes([typeof(InativarClienteHandler), typeof(InativarClienteHandler)]);
        ServiceCollectionExtensions.ApplyRegistrations(services, options);

        await Assert.That(services.Count(d => d.ServiceType == typeof(IRequestHandler<InativarClienteCommand, Result>))).IsEqualTo(1);
    }

    [Test]
    public async Task AddBehavior_rejeita_tipo_que_nao_e_behavior_generico_aberto()
    {
        var options = new CqrsOptions();

        await Assert.That(() => options.AddBehavior(typeof(string))).Throws<ArgumentException>();
        await Assert.That(() => options.AddBehavior(typeof(RegistroBehavior<CriarClienteCommand, Result<Guid>>))).Throws<ArgumentException>();
    }

    [Test]
    public async Task SlowRequestThreshold_rejeita_valor_nao_positivo()
    {
        var options = new CqrsOptions();

        await Assert.That(() => options.SlowRequestThreshold = TimeSpan.Zero).ThrowsExactly<ArgumentOutOfRangeException>();
        options.SlowRequestThreshold = null;
        await Assert.That(options.SlowRequestThreshold).IsNull();
    }

    [Test]
    public async Task FindRequestsWithoutHandler_aponta_requisicoes_sem_handler()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>());

        var missing = CqrsDiagnostics.FindRequestsWithoutHandler(services, typeof(CallLog).Assembly);

        await Assert.That(missing).IsEquivalentTo(new[] { typeof(SemHandlerQuery) }, CollectionOrdering.Matching);
    }


    /// <summary>Genérico aberto: ignorado na varredura; fechado apenas neste teste para simular duplicidade.</summary>
    private sealed class HandlerDuplicado<T> : ICommandHandler<InativarClienteCommand>
    {
        public Task<Result> Handle(InativarClienteCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }
}
