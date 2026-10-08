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
                ctx.Resource is PolicyQuery q && q.Owner == ctx.User.Identity?.Name)));
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
    public async Task AuthorizeRequest_without_user_returns_Unauthorized()
    {
        var result = await SendAsync(new AuthenticatedQuery(), ClaimsPrincipalOrNull.Anonymous);

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    [Test]
    public async Task AuthorizeRequest_with_unauthenticated_user_returns_Unauthorized()
    {
        var result = await SendAsync(new AuthenticatedQuery(), new(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    [Test]
    public async Task AuthorizeRequest_with_authenticated_user_executes()
    {
        var result = await SendAsync(new AuthenticatedQuery(), new(FakePrincipalAccessor.User("maria")));

        await Assert.That(result.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Authorization_happens_before_validation_and_does_not_reveal_rules()
    {
        // Motivo vazio seria erro de validação; sem autenticação, a resposta deve ser apenas 401
        var result = await SendAsync(new RefundCommand(""), ClaimsPrincipalOrNull.Anonymous);

        await Assert.That(result.Errors.Select(e => e.Type)).IsEquivalentTo(new[] { ErrorType.Unauthorized }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Roles_without_the_role_returns_Forbidden() =>
        await Assert.That((await SendAsync(new RefundCommand("x"), new(FakePrincipalAccessor.User("maria", "Vendas")))).Error!.Type)
            .IsEqualTo(ErrorType.Forbidden);

    [Test]
    public async Task Roles_with_one_of_the_roles_executes() =>
        await Assert.That((await SendAsync(new RefundCommand("x"), new(FakePrincipalAccessor.User("maria", "Financeiro")))).IsSuccess)
            .IsTrue();

    [Test]
    public async Task Policy_receives_the_request_as_resource()
    {
        var owner = await SendAsync(new PolicyQuery("maria"), new(FakePrincipalAccessor.User("maria")));
        var other = await SendAsync(new PolicyQuery("joao"), new(FakePrincipalAccessor.User("maria")));

        await Assert.That(owner.IsSuccess).IsTrue();
        await Assert.That(other.Error!.Type).IsEqualTo(ErrorType.Forbidden);
    }

    [Test]
    public async Task Policy_without_AddAuthorization_throws_clear_exception()
    {
        using var provider = TestHost.Build(services: s =>
            s.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = FakePrincipalAccessor.User("maria") }));
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PolicyQuery("maria")); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("AddAuthorization");
    }

    [Test]
    public async Task Denying_RequestAuthorizer_stops_without_calling_the_handler()
    {
        using var provider = Build(new FakePrincipalAccessor());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetOrderQuery(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("PEDIDO_NAO_ENCONTRADO");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain("handler");
    }

    [Test]
    public async Task Allowing_RequestAuthorizer_executes_the_handler() =>
        await Assert.That((await SendAsync(new GetOrderQuery(Guid.NewGuid()), ClaimsPrincipalOrNull.Anonymous)).IsSuccess).IsTrue();

    [Test]
    public async Task RequireAuthorization_is_enabled_by_default() =>
        await Assert.That(new CqrsOptions().RequireAuthorization).IsTrue();

    /// <summary>Requisição de fora dos assemblies varridos (handler registrado à mão): barrada em execução.</summary>
    private static ServiceProvider BuildWithUndeclaredRequest(Action<CqrsOptions>? configure = null) =>
        TestHost.Build(configure, services: s =>
            s.AddScoped<IRequestHandler<NoAuthorizationQuery<int>, Result<int>>, NoAuthorizationHandler<int>>());

    [Test]
    public async Task RequireAuthorization_blocks_at_runtime_request_without_declared_authorization()
    {
        using var provider = BuildWithUndeclaredRequest();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () =>
            { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoAuthorizationQuery<int>()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("não declara autorização");
    }

    [Test]
    public async Task RequireAuthorization_fails_at_startup_listing_requests_without_authorization()
    {
        var (services, options) = Register(
            typeof(NoAuthorizationHandler<int>), typeof(PublishesHandler), typeof(GetOrderHandler), typeof(GetOrderAuthorizer));

        var ex = await Assert.That(() =>
                ServiceCollectionExtensions.EnsureAuthorizationIsDeclared(services, ServiceCollectionExtensions.GetKnownRequests(options)))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(NoAuthorizationQuery<int>).FullName!);
        await Assert.That(ex.Message).DoesNotContain(typeof(PublishesQuery).FullName!);
        await Assert.That(ex.Message).DoesNotContain(typeof(GetOrderQuery).FullName!);
    }

    [Test]
    public async Task RequireAuthorization_fails_at_startup_also_with_explicit_registration()
    {
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<NoAuthorizationQuery<int>, int, NoAuthorizationHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(NoAuthorizationQuery<int>).FullName!);
    }

    [Test]
    public async Task Policy_without_IRequestPolicyEvaluator_throws_clear_exception()
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
    public async Task Policy_uses_custom_IRequestPolicyEvaluator_without_AspNetCore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = FakePrincipalAccessor.User("maria") });
        services.AddSingleton<IRequestPolicyEvaluator, OwnerPolicyEvaluator>();
        services.AddTecCqrs(o => o.AddQueryHandler<PolicyQuery, int, PolicyHandler>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Assert.That((await sender.Send(new PolicyQuery("maria"))).IsSuccess).IsTrue();
        await Assert.That((await sender.Send(new PolicyQuery("joao"))).Error!.Type).IsEqualTo(ErrorType.Forbidden);
    }

    [Test]
    public async Task Without_IPrincipalAccessor_AuthorizeRequest_returns_Unauthorized()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.AddQueryHandler<AuthenticatedQuery, int, AuthenticatedHandler>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AuthenticatedQuery());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    /// <summary>Policy "SomenteDono" sem ASP.NET Core.</summary>
    private sealed class OwnerPolicyEvaluator : IRequestPolicyEvaluator
    {
        public Task<bool> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object request, string policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(policy == PolicyQuery.PolicyName && request is PolicyQuery q && q.Owner == user.Identity?.Name);
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
    public async Task RequireAuthorization_allows_AllowAnonymousRequest() =>
        await Assert.That((await SendAsync(new PublishesQuery(), ClaimsPrincipalOrNull.Anonymous)).IsSuccess).IsTrue();

    [Test]
    public async Task Without_RequireAuthorization_request_without_authorization_executes()
    {
        using var provider = BuildWithUndeclaredRequest(o => o.RequireAuthorization = false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoAuthorizationQuery<int>());

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Type_and_interface_authorizers_are_executed_in_order()
    {
        using var provider = Build(new FakePrincipalAccessor());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelOrderCommand(Guid.NewGuid()));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "authorizer:concreto", "authorizer:interface", "handler" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Denying_interface_authorizer_blocks_the_request()
    {
        // Risco: ignorar o authorizer de IPedidoDoCliente e rodar o handler (fail-open)
        using var provider = Build(new FakePrincipalAccessor());
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelOrderCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("PEDIDO_DE_OUTRO_CLIENTE");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain("handler");
    }

    [Test]
    public async Task Base_class_authorizer_is_applied_to_derived_types()
    {
        var denied = await SendAsync(new DeleteAccountCommand(Allowed: false), ClaimsPrincipalOrNull.Anonymous);
        var allowed = await SendAsync(new DeleteAccountCommand(Allowed: true), ClaimsPrincipalOrNull.Anonymous);

        await Assert.That(denied.Error!.Code).IsEqualTo("NAO_AUDITADO");
        await Assert.That(allowed.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Interface_authorizer_registered_directly_in_container_fails_at_startup()
    {
        // Risco: contar como autorização declarada na inicialização sem nunca ser executado (só o tipo exato e os alvos
        // registrados pelo AddTecCqrs entram em execução): a requisição rodaria sem a verificação de posse (fail-open)
        var services = new ServiceCollection();
        services.AddScoped<IRequestAuthorizer<ICustomerResource>, DenyAuthorizer<ICustomerResource>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddCommandHandler<AccessResourceCommand<int>, AccessResourceHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(ICustomerResource).FullName!);
        await Assert.That(ex.Message).Contains("AddRequestAuthorizer");
    }

    [Test]
    public async Task Authorizer_for_unknown_request_type_registered_directly_in_container_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestAuthorizer<AccessResourceCommand<string>>, DenyAuthorizer<AccessResourceCommand<string>>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("AddRequestAuthorizer");
    }

    [Test]
    public async Task Interface_authorizer_registered_via_options_is_executed()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o
            .AddCommandHandler<AccessResourceCommand<int>, AccessResourceHandler<int>>()
            .AddRequestAuthorizer<ICustomerResource, DenyAuthorizer<ICustomerResource>>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AccessResourceCommand<int>());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE");
    }

    [Test]
    public async Task Exact_type_authorizer_registered_directly_in_container_is_accepted_and_executed()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestAuthorizer<AccessResourceCommand<int>>, DenyAuthorizer<AccessResourceCommand<int>>>();
        services.AddTecCqrs(o => o.AddCommandHandler<AccessResourceCommand<int>, AccessResourceHandler<int>>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AccessResourceCommand<int>());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE");
    }

    [Test]
    [Arguments(",")]
    [Arguments(" , ")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Roles_without_any_role_throws_instead_of_requiring_only_authentication(string roles)
    {
        // Risco: com a lista de papéis vazia, a regra aceitaria qualquer usuário autenticado
        var ex = await Assert.That(() => AuthorizeRule.From(typeof(RefundCommand), new AuthorizeRequestAttribute { Roles = roles }))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Roles");
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task Blank_policy_throws_instead_of_requiring_only_authentication(string policy)
    {
        var ex = await Assert.That(() => AuthorizeRule.From(typeof(PolicyQuery), new AuthorizeRequestAttribute { Policy = policy }))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Policy");
    }

    [Test]
    public async Task Valid_roles_and_policy_are_kept()
    {
        var rule = AuthorizeRule.From(typeof(RefundCommand), new AuthorizeRequestAttribute { Roles = "Admin, Financeiro,", Policy = "P" });
        var noRules = AuthorizeRule.From(typeof(AuthenticatedQuery), new AuthorizeRequestAttribute());

        await Assert.That(rule.Roles).IsEquivalentTo(new[] { "Admin", "Financeiro" }, CollectionOrdering.Matching);
        await Assert.That(rule.Policy).IsEqualTo("P");
        await Assert.That(noRules.Roles).IsEmpty();
        await Assert.That(noRules.Policy).IsNull();
    }

    [Test]
    public async Task Default_principal_comes_from_HttpContext()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = FakePrincipalAccessor.User("maria") };

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AuthenticatedQuery());

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
    public async Task Command_without_validator_throws_by_default()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoValidatorCommand()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(NoValidatorCommand));
    }

    [Test]
    public async Task Command_without_validator_executes_when_requirement_is_disabled()
    {
        using var provider = TestHost.Build(o => o.RequireValidatorForCommands = false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoValidatorCommand());

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Query_without_validator_is_not_required()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetCustomerQuery(Guid.NewGuid()));

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task ValidationException_thrown_in_handler_becomes_validation_failure()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ValidateInHandlerCommand());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Validation);
        await Assert.That(result.Error.Code).IsEqualTo("CEP_INVALIDO");
        await Assert.That(result.Error.Field).IsEqualTo("endereco.cep");
    }

    [Test]
    public async Task FindCommandsWithoutValidator_reports_commands_without_validator()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()).AddFluentValidation();

        var missing = CqrsDiagnostics.FindCommandsWithoutValidator(services, typeof(CallLog).Assembly);

        await Assert.That(missing).IsEquivalentTo(new[] { typeof(NoValidatorCommand) }, CollectionOrdering.Matching);
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
    public async Task Publishes_only_after_commit() =>
        await Assert.That(await RunAsync(new CreateWithEventCommand()))
            .IsEquivalentTo(new[] { "begin", "handler", "commit", "crm", "email" }, CollectionOrdering.Matching);

    [Test]
    public async Task Failure_discards_notifications() =>
        await Assert.That(await RunAsync(new CreateWithEventCommand(Fail: true)))
            .IsEquivalentTo(new[] { "begin", "handler", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Exception_discards_notifications() =>
        await Assert.That(await RunAsync(new CreateWithEventCommand(Throw: true)))
            .IsEquivalentTo(new[] { "begin", "handler", "rollback" }, CollectionOrdering.Matching);

    [Test]
    public async Task Without_transaction_publishes_at_the_end_on_success() =>
        await Assert.That(await RunAsync(new CreateWithEventCommand(), withUnitOfWork: false))
            .IsEquivalentTo(new[] { "handler", "crm", "email" }, CollectionOrdering.Matching);

    [Test]
    public async Task Nested_command_publishes_after_outer_command_commit() =>
        await Assert.That(await RunAsync(new ParentWithEventCommand(ChildFails: false)))
            .IsEquivalentTo(new[] { "begin", "handler", "pai", "commit", "crm", "email" }, CollectionOrdering.Matching);

    [Test]
    public async Task Failed_nested_command_rolls_back_outer_transaction_and_discards_notifications()
    {
        // Risco: o pai ignorar a falha do filho e fazer commit do que o filho gravou pela metade
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ParentWithEventCommand(ChildFails: true));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error!.Code).IsEqualTo("FALHOU"); // erro do command interno
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "handler", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Without_transaction_nested_command_failure_discards_only_its_notifications() =>
        await Assert.That(await RunAsync(new ParentWithEventCommand(ChildFails: true), withUnitOfWork: false))
            .IsEquivalentTo(new[] { "handler", "pai" }, CollectionOrdering.Matching);

    [Test]
    public async Task PublishAfterCommit_in_behavior_is_published_after_commit()
    {
        // O controle de profundidade fica no mediator: um behavior anterior ao TransactionBehavior não recebe exceção
        using var provider = TestHost.Build(o => o.AddBehavior(typeof(PublishInBehavior<,>)), withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("Maria", "12345678909"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "handler", "commit", "crm", "email" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Failure_in_one_handler_does_not_stop_the_others_nor_change_the_result()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishFailingEventCommand());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "commit", "segundo" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PublishAfterCommit_outside_pipeline_throws()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await Assert.That(() => scope.ServiceProvider.GetRequiredService<IPublisher>().PublishAfterCommit(new CustomerCreatedEvent(Guid.NewGuid())))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Failure_creating_handler_after_commit_is_logged_without_reaching_caller()
    {
        var logs = new ListLoggerProvider();
        using var provider = TestHost.Build(withUnitOfWork: true, services: s =>
        {
            s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
            s.AddScoped<INotificationHandler<EventWithBrokenHandler>>(_ => throw new InvalidOperationException("dependência ausente"));
        });
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishBrokenEventCommand());

        await Assert.That(result.IsSuccess).IsTrue();
        // Os handlers da interface (resolvidos à parte) continuam rodando
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "commit", "auditoria", "duplo:interface" }, CollectionOrdering.Matching);
        await Assert.That(logs.Entries.Count(e => e.EventId.Id == 1009 && e.Exception is InvalidOperationException)).IsEqualTo(1);
    }
}

public class NestedTransactionTests
{
    private static async Task<(Result Result, IReadOnlyList<string> Calls)> RunAsync(ChildFailureMode mode)
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ParentIgnoresChildFailureCommand(mode));
        return (result, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
    }

    [Test]
    public async Task Nested_command_exception_handled_by_parent_rolls_back_transaction()
    {
        var (result, calls) = await RunAsync(ChildFailureMode.Exception);

        await Assert.That(result.Error!.Code).IsEqualTo("TRANSACAO_DESFEITA");
        await Assert.That(result.Error.Type).IsEqualTo(ErrorType.Failure);
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "handler", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Nested_command_validation_failure_rolls_back_transaction()
    {
        var (result, calls) = await RunAsync(ChildFailureMode.Validation);

        await Assert.That(result.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "NOME_OBRIGATORIO", "CPF_INVALIDO" }, CollectionOrdering.Matching);
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Failed_nested_command_discards_parent_notifications()
    {
        var (_, calls) = await RunAsync(ChildFailureMode.Failure);

        await Assert.That(calls).DoesNotContain("email");
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "handler", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Failed_nested_command_with_SkipTransaction_rolls_back_parent_transaction()
    {
        // Risco: o [SkipTransaction] do interno fazer a falha dele ser ignorada e o pai confirmar a transação
        var (result, calls) = await RunAsync(ChildFailureMode.NoTransactionFailure);

        await Assert.That(result.Error!.Code).IsEqualTo("FALHOU_SEM_TRANSACAO"); // erro do command interno
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Nested_SkipTransaction_command_exception_handled_by_parent_rolls_back_transaction()
    {
        var (result, calls) = await RunAsync(ChildFailureMode.NoTransactionException);

        await Assert.That(result.Error!.Code).IsEqualTo("TRANSACAO_DESFEITA");
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "rollback" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Failed_nested_query_does_not_roll_back_transaction()
    {
        var (result, calls) = await RunAsync(ChildFailureMode.QueryNotFound);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "pai", "commit", "crm", "email" }, CollectionOrdering.Matching);
    }
}

public class ConcurrencyTests
{
    [Test]
    public async Task Parallel_Sends_in_same_scope_do_not_mix_notifications()
    {
        // Risco: com profundidade e fila compartilhadas, a falha de A descartaria a notificação de B (ou B publicaria a de A)
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var results = await Task.WhenAll(
            sender.Send(new ParallelCommand("A", DelayMs: 80, Fail: true)),
            sender.Send(new ParallelCommand("B", DelayMs: 10)));

        await Assert.That(results[0].IsFailure).IsTrue();
        await Assert.That(results[1].IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "evento:B" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Successful_parallel_Sends_each_publish_their_own_notifications()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => sender.Send(new ParallelCommand($"{i}", DelayMs: 10 - i))));

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(Enumerable.Range(0, 10).Select(i => $"evento:{i}"), CollectionOrdering.Any);
    }

    [Test]
    public async Task Parallel_transactional_commands_in_same_scope_are_rejected()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var ex = await Assert.That(async () =>
            {
                await Task.WhenAll(
                    sender.Send(new ParallelCommand("A", DelayMs: 50)),
                    sender.Send(new ParallelCommand("B", DelayMs: 0)));
            })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("em paralelo");
    }
}

public class TransactionSafetyTests
{
    [Test]
    public async Task Commit_does_not_receive_the_request_token()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        using var cts = new CancellationTokenSource();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("Maria", "12345678909"), cts.Token);

        await Assert.That(scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().CommitToken.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task Rollback_failure_does_not_replace_failure_result()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().FailOnRollback = true;

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new DeactivateCustomerCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("CLIENTE_JA_INATIVO");
    }

    [Test]
    public async Task Handler_returning_null_rolls_back_transaction()
    {
        // Risco: o null estourar fora do try do rollback (NullReferenceException) e a transação ficar aberta
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ReturnsNullCommand()); })
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
    public async Task Request_that_is_both_command_and_query_fails_at_registration()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(CommandAndQueryHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("command e query");
    }

    [Test]
    public async Task AllowAnonymous_with_AuthorizeRequest_fails_at_registration()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(AnonymousAndAuthorizedHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("AllowAnonymousRequest");
    }

    [Test]
    public async Task Request_without_command_or_query_marker_fails_at_registration()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(NoMarkerHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("ICommand");
    }

    [Test]
    public async Task Blank_roles_fails_at_scan_registration()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(BlankRolesHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Roles");
        await Assert.That(ex.Message).Contains("BlankRolesQuery");
    }

    [Test]
    public async Task Blank_policy_fails_at_explicit_registration()
    {
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<BlankPolicyQuery<int>, int, BlankPolicyHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Policy");
    }

    [Test]
    public async Task Request_without_marker_with_handler_registered_directly_in_container_fails_on_execution()
    {
        // As verificações de marcações valem também para handlers registrados direto no container
        using var provider = TestHost.Build(services: s =>
            s.AddScoped<IRequestHandler<NoMarkerRequest<int>, Result>, NoMarkerHandler<int>>());
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoMarkerRequest<int>()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("ICommand, ICommand<T> ou IQuery<T>");
    }

    [Test]
    public async Task Blank_roles_with_handler_registered_directly_in_container_fails_on_execution()
    {
        using var provider = TestHost.Build(services: s =>
            s.AddScoped<IRequestHandler<BlankRolesQuery<int>, Result<int>>, BlankRolesHandler<int>>());
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new BlankRolesQuery<int>()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Roles");
    }

    [Test]
    public async Task Custom_interface_validator_registered_via_options_fails_at_startup()
    {
        // Risco: aceitar o IRequestValidator<IPossuiDocumentoRequest> sem nunca executá-lo (o pipeline só usa o tipo exato)
        var services = new ServiceCollection();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o
                .AddCommandHandler<WithDocumentCommand<int>, WithDocumentHandler<int>>()
                .AddRequestAuthorizer<WithDocumentCommand<int>, AllowAllAuthorizer<WithDocumentCommand<int>>>()
                .AddRequestValidator<IHasDocumentRequest, RequiredDocumentValidator<IHasDocumentRequest>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(IHasDocumentRequest).FullName!);
        await Assert.That(ex.Message).Contains("nunca seria executado");
    }

    [Test]
    public async Task Custom_interface_validator_registered_directly_in_container_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestValidator<IHasDocumentRequest>, RequiredDocumentValidator<IHasDocumentRequest>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o
                .AddCommandHandler<WithDocumentCommand<int>, WithDocumentHandler<int>>()
                .AddRequestAuthorizer<WithDocumentCommand<int>, AllowAllAuthorizer<WithDocumentCommand<int>>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(IHasDocumentRequest).FullName!);
    }

    [Test]
    public async Task Custom_interface_validator_found_by_scan_fails_at_startup()
    {
        var (services, options) = AuthorizationTests.Register(
            typeof(WithDocumentHandler<int>), typeof(RequiredDocumentValidator<IHasDocumentRequest>));

        await Assert.That(() => ServiceCollectionExtensions.EnsureValidatorsAreApplied(services, options))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Custom_exact_type_validator_is_accepted_and_executed()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o
            .AddCommandHandler<WithDocumentCommand<int>, WithDocumentHandler<int>>()
            .AddRequestAuthorizer<WithDocumentCommand<int>, AllowAllAuthorizer<WithDocumentCommand<int>>>()
            .AddRequestValidator<WithDocumentCommand<int>, RequiredDocumentValidator<WithDocumentCommand<int>>>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new WithDocumentCommand<int>(""));

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
    public async Task Interface_only_validator_fails_at_startup()
    {
        var (services, options) = RegisterValidators(typeof(WithDocumentCommand<int>), typeof(DocumentValidator<int>));

        var ex = await Assert.That(() => CqrsBuilderFluentValidationExtensions.EnsureValidatorsAreApplied(services, options.ScannedRequests))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Include(new DocumentValidator");
    }

    [Test]
    public async Task Interface_validator_with_custom_validator_is_accepted()
    {
        var (services, options) = RegisterValidators(
            typeof(WithDocumentCommand<int>), typeof(DocumentValidator<int>), typeof(WithDocumentValidator<int>));

        await Assert.That(() => CqrsBuilderFluentValidationExtensions.EnsureValidatorsAreApplied(services, options.ScannedRequests))
            .ThrowsNothing();
    }

    [Test]
    public async Task Interface_only_validator_for_request_with_handler_fails_in_AddFluentValidation()
    {
        // A requisição vem do handler registrado explicitamente, não da varredura
        var services = new ServiceCollection();
        var builder = services.AddTecCqrs(o => o
            .AddCommandHandler<WithDocumentCommand<int>, WithDocumentHandler<int>>()
            .AddRequestAuthorizer<WithDocumentCommand<int>, AllowAllAuthorizer<WithDocumentCommand<int>>>());

        var ex = await Assert.That(() => builder.AddFluentValidation(fv => fv.AddValidator<IHasDocumentRequest, DocumentRequestValidator<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("WithDocumentCommand");
    }

    [Test]
    public async Task FindRequestsWithoutAuthorization_ignores_requests_with_declared_authorization()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>());

        var missing = CqrsDiagnostics.FindRequestsWithoutAuthorization(services, typeof(CallLog).Assembly);

        // Todas as requisições do assembly de testes declaram autorização: atributo, authorizer do tipo
        // (ObterPedidoQuery), de interface (CancelarPedidoCommand) ou de classe base (ExcluirContaCommand)
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task FindRequestsWithoutAuthorization_reports_request_without_authorization()
    {
        var (services, options) = AuthorizationTests.Register(
            typeof(NoAuthorizationHandler<int>), typeof(CancelOrderHandler), typeof(CustomerOrderAuthorizer));

        var missing = ServiceCollectionExtensions.FindRequestsWithoutAuthorization(services, ServiceCollectionExtensions.GetKnownRequests(options));

        await Assert.That(missing).IsEquivalentTo(new[] { typeof(NoAuthorizationQuery<int>) }, CollectionOrdering.Matching);
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
    public async Task ValidationException_becomes_400_with_errors_per_field()
    {
        var (status, body) = await HandleAsync(new ValidationException(
            [new ValidationFailure("Endereco.Cep", "CEP inválido.") { ErrorCode = "CEP_INVALIDO" }]));

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.GetProperty("errors")[0].GetProperty("code").GetString()).IsEqualTo("CEP_INVALIDO");
        await Assert.That(body.GetProperty("errors")[0].GetProperty("field").GetString()).IsEqualTo("endereco.cep");
    }

    [Test]
    public async Task ValidationException_without_failures_becomes_generic_400()
    {
        var (status, body) = await HandleAsync(new ValidationException("detalhe interno"));

        await Assert.That(status).IsEqualTo(400);
        await Assert.That(body.GetRawText()).DoesNotContain("detalhe interno");
    }

    [Test]
    public async Task BadHttpRequestException_uses_its_own_status_without_exposing_the_message()
    {
        var (status, body) = await HandleAsync(new BadHttpRequestException("Failed to read parameter \"SenhaInterna\"", 413));

        await Assert.That(status).IsEqualTo(413);
        await Assert.That(body.GetRawText()).DoesNotContain("SenhaInterna");
    }

    [Test]
    public async Task BadHttpRequestException_with_status_outside_4xx_becomes_400() =>
        await Assert.That((await HandleAsync(new BadHttpRequestException("x", 500))).Status).IsEqualTo(400);

    [Test]
    public async Task ValidationException_without_AddFluentValidation_becomes_generic_500()
    {
        // Sem o mapper do TEC.Cqrs.FluentValidation, a exceção é desconhecida: 500 sem detalhes
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = new ValidationException("detalhe"), Path = "/" });

        await ApplicationBuilderExtensions.HandleExceptionAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(500);
    }

    [Test]
    public async Task Client_errors_are_not_logged_as_server_errors()
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
    public async Task Location_that_is_not_a_relative_path_is_discarded_without_throwing(string location)
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
    public async Task Location_with_space_or_accent_is_escaped(string location, string expected)
    {
        // Risco: ArgumentException depois do commit (cliente receberia 500 com o recurso já criado)
        var result = Result.Success(1).ToCreatedHttpResult(_ => location);

        await Assert.That(result.Location).IsEqualTo(expected);
        await Assert.That(result.StatusCode).IsEqualTo(201);
    }

    [Test]
    public async Task Location_discarded_without_RequestServices_does_not_throw()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await Result.Success(1).ToCreatedHttpResult(_ => "https://evil.com").ExecuteAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(201);
    }

    [Test]
    public async Task Location_accepts_relative_path_with_query_string()
    {
        var result = Result.Success(1).ToCreatedHttpResult(id => $"/clientes/{id}?versao=2");

        await Assert.That(result.Location).IsEqualTo("/clientes/1?versao=2");
    }

    [Test]
    public async Task Location_is_not_validated_on_failure()
    {
        var result = Result.Failure<int>(Error.Conflict("X", "x")).ToCreatedHttpResult(_ => "https://evil.com");

        await Assert.That(result.Location).IsNull();
    }

    [Test]
    public async Task Result_with_internal_error_in_any_position_does_not_expose_details()
    {
        // Depende do TEC.Core considerar todos os erros (não só o primeiro) para decidir a exposição
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
