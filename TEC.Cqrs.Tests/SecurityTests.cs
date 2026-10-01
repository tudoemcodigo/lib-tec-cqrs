using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Tests.Fakes;
using TEC.Cqrs.Validation;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class AuthorizationTests
{
    private static ServiceProvider Build(FakePrincipalAccessor principal, Action<CqrsOptions>? configure = null) =>
        TestHost.Build(configure, services: s =>
        {
            s.AddSingleton<IPrincipalAccessor>(principal);
            s.AddAuthorizationCore(o => o.AddPolicy(PolicyQuery.PolicyName, p => p.RequireAssertion(ctx =>
                ctx.Resource is PolicyQuery q && q.Dono == ctx.User.Identity?.Name)));
        });

    private static async Task<Result> SendAsync(IRequest<Result> request, ClaimsPrincipalOrNull user, Action<CqrsOptions>? configure = null)
    {
        using var provider = Build(new FakePrincipalAccessor { Principal = user.Value }, configure);
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static async Task<Result<int>> SendAsync(IRequest<Result<int>> request, ClaimsPrincipalOrNull user, Action<CqrsOptions>? configure = null)
    {
        using var provider = Build(new FakePrincipalAccessor { Principal = user.Value }, configure);
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    [Test]
    public async Task AuthorizeRequest_sem_usuario_retorna_Unauthorized()
    {
        var result = await SendAsync(new AutenticadoQuery(), ClaimsPrincipalOrNull.Anonymous);

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    [Test]
    public async Task AuthorizeRequest_com_usuario_nao_autenticado_retorna_Unauthorized()
    {
        var result = await SendAsync(new AutenticadoQuery(), new(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    [Test]
    public async Task AuthorizeRequest_com_usuario_autenticado_executa()
    {
        var result = await SendAsync(new AutenticadoQuery(), new(FakePrincipalAccessor.User("maria")));

        await Assert.That(result.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Autorizacao_ocorre_antes_da_validacao_e_nao_revela_regras()
    {
        // Motivo vazio seria erro de validação; sem autenticação, a resposta deve ser apenas 401
        var result = await SendAsync(new EstornarCommand(""), ClaimsPrincipalOrNull.Anonymous);

        await Assert.That(result.Errors.Select(e => e.Type)).IsEquivalentTo(new[] { ErrorType.Unauthorized }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Roles_sem_o_papel_retorna_Forbidden() =>
        await Assert.That((await SendAsync(new EstornarCommand("x"), new(FakePrincipalAccessor.User("maria", "Vendas")))).Error!.Type)
            .IsEqualTo(ErrorType.Forbidden);

    [Test]
    public async Task Roles_com_um_dos_papeis_executa() =>
        await Assert.That((await SendAsync(new EstornarCommand("x"), new(FakePrincipalAccessor.User("maria", "Financeiro")))).IsSuccess)
            .IsTrue();

    [Test]
    public async Task Policy_recebe_a_requisicao_como_recurso()
    {
        var dono = await SendAsync(new PolicyQuery("maria"), new(FakePrincipalAccessor.User("maria")));
        var outro = await SendAsync(new PolicyQuery("joao"), new(FakePrincipalAccessor.User("maria")));

        await Assert.That(dono.IsSuccess).IsTrue();
        await Assert.That(outro.Error!.Type).IsEqualTo(ErrorType.Forbidden);
    }

    [Test]
    public async Task Policy_sem_AddAuthorization_lanca_excecao_clara()
    {
        using var provider = TestHost.Build(services: s =>
            s.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = FakePrincipalAccessor.User("maria") }));
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PolicyQuery("maria")); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("AddAuthorization");
    }

    [Test]
    public async Task RequestAuthorizer_negado_interrompe_sem_chamar_o_handler()
    {
        using var provider = Build(new FakePrincipalAccessor());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ObterPedidoQuery(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("PEDIDO_NAO_ENCONTRADO");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain("handler");
    }

    [Test]
    public async Task RequestAuthorizer_permitido_executa_o_handler() =>
        await Assert.That((await SendAsync(new ObterPedidoQuery(Guid.NewGuid()), ClaimsPrincipalOrNull.Anonymous)).IsSuccess).IsTrue();

    [Test]
    public async Task RequireAuthorization_e_ativado_por_padrao() =>
        await Assert.That(new CqrsOptions().RequireAuthorization).IsTrue();

    /// <summary>Requisição de fora dos assemblies varridos (handler registrado à mão): barrada em execução.</summary>
    private static ServiceProvider BuildWithUndeclaredRequest(Action<CqrsOptions>? configure = null) =>
        TestHost.Build(configure, services: s =>
            s.AddScoped<IRequestHandler<SemAutorizacaoQuery<int>, Result<int>>, SemAutorizacaoHandler<int>>());

    [Test]
    public async Task RequireAuthorization_bloqueia_em_execucao_requisicao_sem_autorizacao_declarada()
    {
        using var provider = BuildWithUndeclaredRequest();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () =>
            { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SemAutorizacaoQuery<int>()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("não declara autorização");
    }

    [Test]
    public async Task RequireAuthorization_falha_na_inicializacao_listando_as_requisicoes_sem_autorizacao()
    {
        var (services, options) = Register(
            typeof(SemAutorizacaoHandler<int>), typeof(PublicaHandler), typeof(ObterPedidoHandler), typeof(ObterPedidoAuthorizer));

        var ex = await Assert.That(() =>
                ServiceCollectionExtensions.EnsureAuthorizationIsDeclared(services, ServiceCollectionExtensions.GetKnownRequests(options)))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(SemAutorizacaoQuery<int>).FullName!);
        await Assert.That(ex.Message).DoesNotContain(typeof(PublicaQuery).FullName!);
        await Assert.That(ex.Message).DoesNotContain(typeof(ObterPedidoQuery).FullName!);
    }

    [Test]
    public async Task RequireAuthorization_falha_na_inicializacao_tambem_no_registro_explicito()
    {
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<SemAutorizacaoQuery<int>, int, SemAutorizacaoHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(SemAutorizacaoQuery<int>).FullName!);
    }

    [Test]
    public async Task Policy_sem_IRequestPolicyEvaluator_lanca_excecao_clara()
    {
        // Só o núcleo (sem AddAspNetCore): a policy não pode ser ignorada (fail closed)
        var services = new ServiceCollection();
        services.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = FakePrincipalAccessor.User("maria") });
        services.AddTecCqrs(o => o.AddQueryHandler<PolicyQuery, int, PolicyHandler>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PolicyQuery("maria")); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("IRequestPolicyEvaluator");
    }

    [Test]
    public async Task Policy_usa_IRequestPolicyEvaluator_proprio_sem_AspNetCore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = FakePrincipalAccessor.User("maria") });
        services.AddSingleton<IRequestPolicyEvaluator, DonoPolicyEvaluator>();
        services.AddTecCqrs(o => o.AddQueryHandler<PolicyQuery, int, PolicyHandler>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.That((await sender.Send(new PolicyQuery("maria"))).IsSuccess).IsTrue();
        await Assert.That((await sender.Send(new PolicyQuery("joao"))).Error!.Type).IsEqualTo(ErrorType.Forbidden);
    }

    [Test]
    public async Task Sem_IPrincipalAccessor_AuthorizeRequest_retorna_Unauthorized()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.AddQueryHandler<AutenticadoQuery, int, AutenticadoHandler>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AutenticadoQuery());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    /// <summary>Policy "SomenteDono" sem ASP.NET Core.</summary>
    private sealed class DonoPolicyEvaluator : IRequestPolicyEvaluator
    {
        public Task<bool> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object request, string policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(policy == PolicyQuery.PolicyName && request is PolicyQuery q && q.Dono == user.Identity?.Name);
    }

    /// <summary>Simula a varredura de <paramref name="types"/> e aplica os registros no container.</summary>
    internal static (ServiceCollection Services, CqrsOptions Options) Register(params Type[] types)
    {
        var services = new ServiceCollection();
        var options = new CqrsOptions();
        options.RegisterTypes(types);
        ServiceCollectionExtensions.ApplyRegistrations(services, options);
        return (services, options);
    }

    [Test]
    public async Task RequireAuthorization_permite_AllowAnonymousRequest() =>
        await Assert.That((await SendAsync(new PublicaQuery(), ClaimsPrincipalOrNull.Anonymous)).IsSuccess).IsTrue();

    [Test]
    public async Task Sem_RequireAuthorization_requisicao_sem_autorizacao_executa()
    {
        using var provider = BuildWithUndeclaredRequest(o => o.RequireAuthorization = false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SemAutorizacaoQuery<int>());

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Authorizers_do_tipo_e_da_interface_sao_executados_em_ordem()
    {
        using var provider = Build(new FakePrincipalAccessor());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelarPedidoCommand(Guid.NewGuid()));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "authorizer:concreto", "authorizer:interface", "handler" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Authorizer_de_interface_negado_bloqueia_a_requisicao()
    {
        // Antes: o authorizer de IPedidoDoCliente era ignorado e o handler rodava (fail-open)
        using var provider = Build(new FakePrincipalAccessor());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelarPedidoCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("PEDIDO_DE_OUTRO_CLIENTE");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain("handler");
    }

    [Test]
    public async Task Authorizer_de_classe_base_e_aplicado_as_derivadas()
    {
        var negado = await SendAsync(new ExcluirContaCommand(Permitido: false), ClaimsPrincipalOrNull.Anonymous);
        var permitido = await SendAsync(new ExcluirContaCommand(Permitido: true), ClaimsPrincipalOrNull.Anonymous);

        await Assert.That(negado.Error!.Code).IsEqualTo("NAO_AUDITADO");
        await Assert.That(permitido.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Authorizer_de_interface_registrado_direto_no_container_falha_na_inicializacao()
    {
        // Antes: contava como autorização declarada na inicialização, mas nunca era executado (só o tipo exato e os alvos
        // registrados pelo AddTecCqrs entram em execução): a requisição rodava sem a verificação de posse (fail-open)
        var services = new ServiceCollection();
        services.AddScoped<IRequestAuthorizer<IRecursoDoCliente>, NegarAuthorizer<IRecursoDoCliente>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddCommandHandler<AcessarRecursoCommand<int>, AcessarRecursoHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(IRecursoDoCliente).FullName!);
        await Assert.That(ex.Message).Contains("AddRequestAuthorizer");
    }

    [Test]
    public async Task Authorizer_de_tipo_que_nao_e_requisicao_conhecida_registrado_direto_no_container_falha_na_inicializacao()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestAuthorizer<AcessarRecursoCommand<string>>, NegarAuthorizer<AcessarRecursoCommand<string>>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PublicaQuery, int, PublicaHandler>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("AddRequestAuthorizer");
    }

    [Test]
    public async Task Authorizer_de_interface_registrado_pelas_opcoes_e_executado()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o
            .AddCommandHandler<AcessarRecursoCommand<int>, AcessarRecursoHandler<int>>()
            .AddRequestAuthorizer<IRecursoDoCliente, NegarAuthorizer<IRecursoDoCliente>>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AcessarRecursoCommand<int>());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE");
    }

    [Test]
    public async Task Authorizer_do_tipo_exato_registrado_direto_no_container_e_aceito_e_executado()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestAuthorizer<AcessarRecursoCommand<int>>, NegarAuthorizer<AcessarRecursoCommand<int>>>();
        services.AddTecCqrs(o => o.AddCommandHandler<AcessarRecursoCommand<int>, AcessarRecursoHandler<int>>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AcessarRecursoCommand<int>());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE");
    }

    [Test]
    [Arguments(",")]
    [Arguments(" , ")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Roles_sem_nenhum_papel_lanca_em_vez_de_exigir_so_autenticacao(string roles)
    {
        // Antes: a lista de papéis ficava vazia e a regra passava a aceitar qualquer usuário autenticado
        var ex = await Assert.That(() => AuthorizeRule.From(typeof(EstornarCommand), new AuthorizeRequestAttribute { Roles = roles }))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Roles");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Policy_em_branco_lanca_em_vez_de_exigir_so_autenticacao(string policy)
    {
        var ex = await Assert.That(() => AuthorizeRule.From(typeof(PolicyQuery), new AuthorizeRequestAttribute { Policy = policy }))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Policy");
    }

    [Test]
    public async Task Roles_e_policy_validos_sao_mantidos()
    {
        var rule = AuthorizeRule.From(typeof(EstornarCommand), new AuthorizeRequestAttribute { Roles = "Admin, Financeiro,", Policy = "P" });
        var semRegras = AuthorizeRule.From(typeof(AutenticadoQuery), new AuthorizeRequestAttribute());

        await Assert.That(rule.Roles).IsEquivalentTo(new[] { "Admin", "Financeiro" }, CollectionOrdering.Matching);
        await Assert.That(rule.Policy).IsEqualTo("P");
        await Assert.That(semRegras.Roles).IsEmpty();
        await Assert.That(semRegras.Policy).IsNull();
    }

    [Test]
    public async Task Principal_padrao_vem_do_HttpContext()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = FakePrincipalAccessor.User("maria") };

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AutenticadoQuery());

        await Assert.That(result.IsSuccess).IsTrue();
    }

    /// <summary>Wrapper para passar "sem usuário" de forma explícita nos testes.</summary>
    public readonly record struct ClaimsPrincipalOrNull(System.Security.Claims.ClaimsPrincipal? Value)
    {
        public static ClaimsPrincipalOrNull Anonymous => new(null);
    }
}

public class RequiredValidationTests
{
    [Test]
    public async Task Command_sem_validator_lanca_excecao_por_padrao()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SemValidatorCommand()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(SemValidatorCommand));
    }

    [Test]
    public async Task Command_sem_validator_executa_quando_a_exigencia_esta_desativada()
    {
        using var provider = TestHost.Build(o => o.RequireValidatorForCommands = false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SemValidatorCommand());

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Query_sem_validator_nao_e_exigida()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ObterClienteQuery(Guid.NewGuid()));

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ValidationException_lancada_no_handler_vira_falha_de_validacao()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ValidarNoHandlerCommand());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Validation);
        await Assert.That(result.Error.Code).IsEqualTo("CEP_INVALIDO");
        await Assert.That(result.Error.Field).IsEqualTo("endereco.cep");
    }

    [Test]
    public async Task FindCommandsWithoutValidator_aponta_commands_sem_validator()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()).AddFluentValidation();

        var missing = CqrsDiagnostics.FindCommandsWithoutValidator(services, typeof(CallLog).Assembly);

        await Assert.That(missing).IsEquivalentTo(new[] { typeof(SemValidatorCommand) }, CollectionOrdering.Matching);
    }
}

public class DeferredNotificationTests
{
    private static async Task<IReadOnlyList<string>> RunAsync(IRequest<Result> request, bool withUnitOfWork = true)
    {
        using var provider = TestHost.Build(withUnitOfWork: withUnitOfWork);
        using var scope = provider.CreateScope();

        try
        {
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        }
        catch (InvalidOperationException)
        {
            // Esperado no cenário de exceção; o que interessa é a sequência de chamadas
        }

        return scope.ServiceProvider.GetRequiredService<CallLog>().Entries;
    }

    [Test]
    public async Task Publica_somente_apos_o_commit() =>
        await Assert.That(await RunAsync(new CriarComEventoCommand()))
            .IsEquivalentTo(new[] { "begin", "handler", "commit", "crm", "email" }, CollectionOrdering.Matching);

    [Test]
    public async Task Falha_descarta_as_notificacoes() =>
        await Assert.That(await RunAsync(new CriarComEventoCommand(Falhar: true)))
            .IsEquivalentTo(new[] { "begin", "handler", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Excecao_descarta_as_notificacoes() =>
        await Assert.That(await RunAsync(new CriarComEventoCommand(Lancar: true)))
            .IsEquivalentTo(new[] { "begin", "handler", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Sem_transacao_publica_ao_final_com_sucesso() =>
        await Assert.That(await RunAsync(new CriarComEventoCommand(), withUnitOfWork: false))
            .IsEquivalentTo(new[] { "handler", "crm", "email" }, CollectionOrdering.Matching);

    [Test]
    public async Task Command_aninhado_publica_apos_o_commit_do_command_externo() =>
        await Assert.That(await RunAsync(new PaiComEventoCommand(FilhoFalha: false)))
            .IsEquivalentTo(new[] { "begin", "handler", "pai", "commit", "crm", "email" }, CollectionOrdering.Matching);

    [Test]
    public async Task Falha_do_command_aninhado_desfaz_a_transacao_externa_e_descarta_as_notificacoes()
    {
        // Antes: o pai ignorava a falha do filho e fazia commit do que o filho gravou pela metade
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PaiComEventoCommand(FilhoFalha: true));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error!.Code).IsEqualTo("FALHOU"); // erro do command interno
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "handler", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Sem_transacao_falha_do_command_aninhado_descarta_apenas_as_notificacoes_dele() =>
        await Assert.That(await RunAsync(new PaiComEventoCommand(FilhoFalha: true), withUnitOfWork: false))
            .IsEquivalentTo(new[] { "handler", "pai" }, CollectionOrdering.Matching);

    [Test]
    public async Task PublishAfterCommit_em_behavior_e_publicado_apos_o_commit()
    {
        // Antes: o controle de profundidade ficava no TransactionBehavior e o behavior (anterior a ele) recebia exceção
        using var provider = TestHost.Build(o => o.AddBehavior(typeof(PublicarNoBehavior<,>)), withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CriarClienteCommand("Maria", "12345678909"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "handler", "commit", "crm", "email" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Falha_em_um_handler_nao_impede_os_demais_nem_altera_o_resultado()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublicarEventoComFalhaCommand());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "commit", "segundo" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PublishAfterCommit_fora_do_pipeline_lanca_excecao()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await Assert.That(() => scope.ServiceProvider.GetRequiredService<IPublisher>().PublishAfterCommit(new ClienteCriadoEvent(Guid.NewGuid())))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Falha_ao_criar_handler_apos_o_commit_e_registrada_sem_chegar_ao_chamador()
    {
        var logs = new ListLoggerProvider();
        using var provider = TestHost.Build(withUnitOfWork: true, services: s =>
        {
            s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
            s.AddScoped<INotificationHandler<EventoComHandlerQuebrado>>(_ => throw new InvalidOperationException("dependência ausente"));
        });
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublicarEventoQuebradoCommand());

        await Assert.That(result.IsSuccess).IsTrue();
        // Os handlers da interface (resolvidos à parte) continuam rodando
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "commit", "auditoria", "duplo:interface" }, CollectionOrdering.Matching);
        await Assert.That(logs.Entries.Count(e => e.EventId.Id == 1009 && e.Exception is InvalidOperationException)).IsEqualTo(1);
    }
}

public class NestedTransactionTests
{
    private static async Task<(Result Result, IReadOnlyList<string> Calls)> RunAsync(ModoFalhaDoFilho modo)
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PaiIgnoraFalhaDoFilhoCommand(modo));
        return (result, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
    }

    [Test]
    public async Task Excecao_do_command_aninhado_tratada_pelo_pai_desfaz_a_transacao()
    {
        var (result, calls) = await RunAsync(ModoFalhaDoFilho.Excecao);

        await Assert.That(result.Error!.Code).IsEqualTo("TRANSACAO_DESFEITA");
        await Assert.That(result.Error.Type).IsEqualTo(ErrorType.Failure);
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "handler", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Falha_de_validacao_do_command_aninhado_desfaz_a_transacao()
    {
        var (result, calls) = await RunAsync(ModoFalhaDoFilho.Validacao);

        await Assert.That(result.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "NOME_OBRIGATORIO", "CPF_INVALIDO" }, CollectionOrdering.Matching);
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Falha_de_command_aninhado_descarta_as_notificacoes_do_pai()
    {
        var (_, calls) = await RunAsync(ModoFalhaDoFilho.Falha);

        await Assert.That(calls).DoesNotContain("email");
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "handler", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Falha_de_command_aninhado_com_SkipTransaction_desfaz_a_transacao_do_pai()
    {
        // Antes: o [SkipTransaction] do interno fazia a falha dele ser ignorada e o pai confirmava a transação
        var (result, calls) = await RunAsync(ModoFalhaDoFilho.SemTransacaoFalha);

        await Assert.That(result.Error!.Code).IsEqualTo("FALHOU_SEM_TRANSACAO"); // erro do command interno
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Excecao_de_command_aninhado_com_SkipTransaction_tratada_pelo_pai_desfaz_a_transacao()
    {
        var (result, calls) = await RunAsync(ModoFalhaDoFilho.SemTransacaoExcecao);

        await Assert.That(result.Error!.Code).IsEqualTo("TRANSACAO_DESFEITA");
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Falha_de_query_aninhada_nao_desfaz_a_transacao()
    {
        var (result, calls) = await RunAsync(ModoFalhaDoFilho.QueryNaoEncontrada);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "commit", "crm", "email" }, CollectionOrdering.Matching);
    }
}

public class ConcurrencyTests
{
    [Test]
    public async Task Sends_em_paralelo_no_mesmo_escopo_nao_misturam_notificacoes()
    {
        // Antes: profundidade e fila compartilhadas; a falha de A descartava a notificação de B (ou B publicava a de A)
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var resultados = await Task.WhenAll(
            sender.Send(new ParaleloCommand("A", AtrasoMs: 80, Falhar: true)),
            sender.Send(new ParaleloCommand("B", AtrasoMs: 10)));

        await Assert.That(resultados[0].IsFailure).IsTrue();
        await Assert.That(resultados[1].IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "evento:B" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Sends_em_paralelo_com_sucesso_publicam_cada_um_as_proprias_notificacoes()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => sender.Send(new ParaleloCommand($"{i}", AtrasoMs: 10 - i))));

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(Enumerable.Range(0, 10).Select(i => $"evento:{i}"), CollectionOrdering.Any);
    }

    [Test]
    public async Task Commands_com_transacao_em_paralelo_no_mesmo_escopo_sao_rejeitados()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var ex = await Assert.That(async () =>
            {
                await Task.WhenAll(
                    sender.Send(new ParaleloCommand("A", AtrasoMs: 50)),
                    sender.Send(new ParaleloCommand("B", AtrasoMs: 0)));
            })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("em paralelo");
    }
}

public class TransactionSafetyTests
{
    [Test]
    public async Task Commit_nao_recebe_o_token_da_requisicao()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        using var cts = new CancellationTokenSource();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CriarClienteCommand("Maria", "12345678909"), cts.Token);

        await Assert.That(scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().CommitToken.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task Falha_no_rollback_nao_substitui_o_resultado_de_falha()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().FailOnRollback = true;

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new InativarClienteCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("CLIENTE_JA_INATIVO");
    }

    [Test]
    public async Task Handler_que_retorna_null_desfaz_a_transacao()
    {
        // Antes: o null estourava fora do try do rollback (NullReferenceException) e a transação ficava aberta
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RetornaNullCommand()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("retornou null");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "rollback" }, CollectionOrdering.Matching);
        await Assert.That(scope.ServiceProvider.GetRequiredService<IUnitOfWork>().HasActiveTransaction).IsFalse();
    }
}

public class RequestRegistrationSafetyTests
{
    [Test]
    public async Task Requisicao_command_e_query_ao_mesmo_tempo_falha_no_registro()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(CommandEQueryHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("command e query");
    }

    [Test]
    public async Task AllowAnonymous_com_AuthorizeRequest_falha_no_registro()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(AnonimoEAutorizadoHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("AllowAnonymousRequest");
    }

    [Test]
    public async Task Requisicao_sem_marcador_de_command_ou_query_falha_no_registro()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(SemMarcadorHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("ICommand");
    }

    [Test]
    public async Task Roles_em_branco_falha_no_registro_por_varredura()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(RolesEmBrancoHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Roles");
        await Assert.That(ex.Message).Contains("RolesEmBrancoQuery");
    }

    [Test]
    public async Task Policy_em_branco_falha_no_registro_explicito()
    {
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PolicyEmBrancoQuery<int>, int, PolicyEmBrancoHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Policy");
    }

    [Test]
    public async Task Requisicao_sem_marcador_com_handler_registrado_direto_no_container_falha_ao_executar()
    {
        // Antes: as verificações de marcações só valiam para handlers registrados pelo AddTecCqrs
        using var provider = TestHost.Build(services: s =>
            s.AddScoped<IRequestHandler<SemMarcadorRequest<int>, Result>, SemMarcadorHandler<int>>());
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SemMarcadorRequest<int>()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("ICommand, ICommand<T> ou IQuery<T>");
    }

    [Test]
    public async Task Roles_em_branco_com_handler_registrado_direto_no_container_falha_ao_executar()
    {
        using var provider = TestHost.Build(services: s =>
            s.AddScoped<IRequestHandler<RolesEmBrancoQuery<int>, Result<int>>, RolesEmBrancoHandler<int>>());
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RolesEmBrancoQuery<int>()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Roles");
    }

    [Test]
    public async Task Validador_proprio_de_interface_registrado_pelas_opcoes_falha_na_inicializacao()
    {
        // Antes: o IRequestValidator<IPossuiDocumentoRequest> era aceito e nunca executado (o pipeline só usa o tipo exato)
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o
                .AddCommandHandler<ComDocumentoCommand<int>, ComDocumentoHandler<int>>()
                .AddRequestAuthorizer<ComDocumentoCommand<int>, PermitirTudoAuthorizer<ComDocumentoCommand<int>>>()
                .AddRequestValidator<IPossuiDocumentoRequest, DocumentoObrigatorioValidator<IPossuiDocumentoRequest>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(IPossuiDocumentoRequest).FullName!);
        await Assert.That(ex.Message).Contains("nunca seria executado");
    }

    [Test]
    public async Task Validador_proprio_de_interface_registrado_direto_no_container_falha_na_inicializacao()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestValidator<IPossuiDocumentoRequest>, DocumentoObrigatorioValidator<IPossuiDocumentoRequest>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o
                .AddCommandHandler<ComDocumentoCommand<int>, ComDocumentoHandler<int>>()
                .AddRequestAuthorizer<ComDocumentoCommand<int>, PermitirTudoAuthorizer<ComDocumentoCommand<int>>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(IPossuiDocumentoRequest).FullName!);
    }

    [Test]
    public async Task Validador_proprio_de_interface_encontrado_na_varredura_falha_na_inicializacao()
    {
        var (services, options) = AuthorizationTests.Register(
            typeof(ComDocumentoHandler<int>), typeof(DocumentoObrigatorioValidator<IPossuiDocumentoRequest>));

        await Assert.That(() => ServiceCollectionExtensions.EnsureValidatorsAreApplied(services, options))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Validador_proprio_do_tipo_exato_e_aceito_e_executado()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o
            .AddCommandHandler<ComDocumentoCommand<int>, ComDocumentoHandler<int>>()
            .AddRequestAuthorizer<ComDocumentoCommand<int>, PermitirTudoAuthorizer<ComDocumentoCommand<int>>>()
            .AddRequestValidator<ComDocumentoCommand<int>, DocumentoObrigatorioValidator<ComDocumentoCommand<int>>>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ComDocumentoCommand<int>(""));

        await Assert.That(result.Error!.Code).IsEqualTo("DOCUMENTO_OBRIGATORIO");
    }

    /// <summary>Simula a varredura de validators de <paramref name="types"/> e aplica os registros no container.</summary>
    private static (ServiceCollection Services, CqrsFluentValidationOptions Options) RegisterValidators(params Type[] types)
    {
        var services = new ServiceCollection();
        var options = new CqrsFluentValidationOptions();
        options.RegisterTypes(types);
        foreach (var descriptor in options.Validators.Concat(options.Adapters))
            ((IServiceCollection)services).Add(descriptor);
        return (services, options);
    }

    [Test]
    public async Task Validator_so_de_interface_falha_na_inicializacao()
    {
        var (services, options) = RegisterValidators(typeof(ComDocumentoCommand<int>), typeof(DocumentoValidator<int>));

        var ex = await Assert.That(() => CqrsBuilderFluentValidationExtensions.EnsureValidatorsAreApplied(services, options.ScannedRequests))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Include(new DocumentoValidator");
    }

    [Test]
    public async Task Validator_de_interface_com_validator_proprio_e_aceito()
    {
        var (services, options) = RegisterValidators(
            typeof(ComDocumentoCommand<int>), typeof(DocumentoValidator<int>), typeof(ComDocumentoValidator<int>));

        await Assert.That(() => CqrsBuilderFluentValidationExtensions.EnsureValidatorsAreApplied(services, options.ScannedRequests))
            .ThrowsNothing();
    }

    [Test]
    public async Task Validator_so_de_interface_de_requisicao_com_handler_falha_no_AddFluentValidation()
    {
        // A requisição vem do handler registrado explicitamente, não da varredura
        var services = new ServiceCollection();
        var builder = services.AddTecCqrs(o => o
            .AddCommandHandler<ComDocumentoCommand<int>, ComDocumentoHandler<int>>()
            .AddRequestAuthorizer<ComDocumentoCommand<int>, PermitirTudoAuthorizer<ComDocumentoCommand<int>>>());

        var ex = await Assert.That(() => builder.AddFluentValidation(fv => fv.AddValidator<IPossuiDocumentoRequest, DocumentoRequestValidator<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("ComDocumentoCommand");
    }

    [Test]
    public async Task FindRequestsWithoutAuthorization_ignora_requisicoes_com_autorizacao_declarada()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>());

        var missing = CqrsDiagnostics.FindRequestsWithoutAuthorization(services, typeof(CallLog).Assembly);

        // Todas as requisições do assembly de testes declaram autorização: atributo, authorizer do tipo
        // (ObterPedidoQuery), de interface (CancelarPedidoCommand) ou de classe base (ExcluirContaCommand)
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task FindRequestsWithoutAuthorization_aponta_requisicao_sem_autorizacao()
    {
        var (services, options) = AuthorizationTests.Register(
            typeof(SemAutorizacaoHandler<int>), typeof(CancelarPedidoHandler), typeof(PedidoDoClienteAuthorizer));

        var missing = ServiceCollectionExtensions.FindRequestsWithoutAuthorization(services, ServiceCollectionExtensions.GetKnownRequests(options));

        await Assert.That(missing).IsEquivalentTo(new[] { typeof(SemAutorizacaoQuery<int>) }, CollectionOrdering.Matching);
    }
}

public class ExceptionHandlerSafetyTests
{
    private static async Task<(int Status, JsonElement Body)> HandleAsync(Exception exception)
    {
        // RequestServices como no ASP.NET Core: os mappers de exceção (FluentValidation) vêm do container
        using var provider = TestHost.Build();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = exception, Path = "/" });

        await ApplicationBuilderExtensions.HandleExceptionAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    [Test]
    public async Task ValidationException_vira_400_com_erros_por_campo()
    {
        var (status, body) = await HandleAsync(new ValidationException(
            [new ValidationFailure("Endereco.Cep", "CEP inválido.") { ErrorCode = "CEP_INVALIDO" }]));

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.GetProperty("errors")[0].GetProperty("code").GetString()).IsEqualTo("CEP_INVALIDO");
        await Assert.That(body.GetProperty("errors")[0].GetProperty("field").GetString()).IsEqualTo("endereco.cep");
    }

    [Test]
    public async Task ValidationException_sem_falhas_vira_400_generico()
    {
        var (status, body) = await HandleAsync(new ValidationException("detalhe interno"));

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.GetRawText()).DoesNotContain("detalhe interno");
    }

    [Test]
    public async Task BadHttpRequestException_usa_o_proprio_status_sem_expor_a_mensagem()
    {
        var (status, body) = await HandleAsync(new BadHttpRequestException("Failed to read parameter \"SenhaInterna\"", 413));

        await Assert.That(status).IsEqualTo(413);
        await Assert.That(body.GetRawText()).DoesNotContain("SenhaInterna");
    }

    [Test]
    public async Task BadHttpRequestException_com_status_fora_de_4xx_vira_400() =>
        await Assert.That((await HandleAsync(new BadHttpRequestException("x", 500))).Status).IsEqualTo(400);

    [Test]
    public async Task ValidationException_sem_AddFluentValidation_vira_500_generico()
    {
        // Sem o mapper do TEC.Cqrs.FluentValidation, a exceção é desconhecida: 500 sem detalhes
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = new ValidationException("detalhe"), Path = "/" });

        await ApplicationBuilderExtensions.HandleExceptionAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(500);
    }

    [Test]
    public async Task Erros_do_cliente_nao_sao_registrados_como_erro_do_servidor()
    {
        using var provider = TestHost.Build();
        await Assert.That(ApplicationBuilderExtensions.IsClientError(new BadHttpRequestException("x"))).IsTrue();
        await Assert.That(ApplicationBuilderExtensions.IsClientError(new ValidationException("x"), provider)).IsTrue();
        await Assert.That(ApplicationBuilderExtensions.IsClientError(new ValidationException("x"))).IsFalse();
        await Assert.That(ApplicationBuilderExtensions.IsClientError(new NotFoundException("X", "x"))).IsTrue();
        await Assert.That(ApplicationBuilderExtensions.IsClientError(new IntegrationException("Pagamentos", "x"))).IsFalse();
        await Assert.That(ApplicationBuilderExtensions.IsClientError(new InvalidOperationException("x"))).IsFalse();
    }

    [Test]
    [Arguments("https://evil.com/x")]
    [Arguments("//evil.com/x")]
    [Arguments("/\\evil.com")]
    [Arguments("/clientes/1\r\nSet-Cookie: x=1")]
    [Arguments("clientes/1")]
    [Arguments("")]
    public async Task Location_que_nao_e_caminho_relativo_e_descartado_sem_lancar(string location)
    {
        // O command já foi confirmado: nada de exceção (HTTP 500); a resposta segue 201, sem o cabeçalho, e o aviso vai para o log
        var logs = new ListLoggerProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs)
                .BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();

        var result = Result.Success(1).ToCreatedHttpResult(_ => location);
        await result.ExecuteAsync(context);

        await Assert.That(result.Location).IsNull();
        await Assert.That(context.Response.StatusCode).IsEqualTo(201);
        await Assert.That(context.Response.Headers.ContainsKey("Location")).IsFalse();
        await Assert.That(logs.Entries.Count(e => e.EventId.Id == 1011)).IsEqualTo(1);
        await Assert.That(logs.Entries.Any(e => e.Message.Contains("evil", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("/clientes/João Silva", "/clientes/Jo%C3%A3o%20Silva")]
    [Arguments("/busca?nome=Ana Maria&cidade=São Paulo", "/busca?nome=Ana%20Maria&cidade=S%C3%A3o%20Paulo")]
    [Arguments("/clientes/a%20b", "/clientes/a%20b")]
    [Arguments("/clientes/100%", "/clientes/100%25")]
    [Arguments("/clientes/\"x\"<y>", "/clientes/%22x%22%3Cy%3E")]
    [Arguments("/clientes/😀", "/clientes/%F0%9F%98%80")]
    public async Task Location_com_espaco_ou_acento_e_escapado(string location, string expected)
    {
        // Antes: ArgumentException depois do commit (cliente recebia 500 com o recurso já criado)
        var result = Result.Success(1).ToCreatedHttpResult(_ => location);

        await Assert.That(result.Location).IsEqualTo(expected);
        await Assert.That(result.StatusCode).IsEqualTo(201);
    }

    [Test]
    public async Task Location_descartado_sem_RequestServices_nao_lanca()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await Result.Success(1).ToCreatedHttpResult(_ => "https://evil.com").ExecuteAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(201);
    }

    [Test]
    public async Task Location_aceita_caminho_relativo_com_query_string()
    {
        var result = Result.Success(1).ToCreatedHttpResult(id => $"/clientes/{id}?versao=2");

        await Assert.That(result.Location).IsEqualTo("/clientes/1?versao=2");
    }

    [Test]
    public async Task Location_nao_e_validado_em_falha()
    {
        var result = Result.Failure<int>(Error.Conflict("X", "x")).ToCreatedHttpResult(_ => "https://evil.com");

        await Assert.That(result.Location).IsNull();
    }

    [Test]
    public async Task Result_com_erro_interno_em_qualquer_posicao_nao_expoe_detalhes()
    {
        // Depende da correção do TEC.Core (0.0.1): antes, apenas o primeiro erro decidia a exposição
        var result = Result.Failure(
            Error.Validation("NOME_OBRIGATORIO", "Nome é obrigatório.", "nome"),
            Error.Failure("DB", "Timeout em sql-prod-01"));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await result.ToHttpResult().ExecuteAsync(context);
        context.Response.Body.Position = 0;
        string json = await new StreamReader(context.Response.Body).ReadToEndAsync();

        await Assert.That(context.Response.StatusCode).IsEqualTo(500);
        await Assert.That(json).DoesNotContain("sql-prod-01");
        await Assert.That(json).DoesNotContain("NOME_OBRIGATORIO");
    }
}
