using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Tests.Fakes;
using TEC.Cqrs.Validation;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class SkipTransactionNestedConcurrencyTests
{
    private static async Task<(Result? Result, Exception? Exception, IReadOnlyList<string> Calls)> RunAsync(ChildrenWithoutTransactionMode mode)
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ParentOfChildrenWithoutTransactionCommand(mode));
            return (result, null, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex, scope.ServiceProvider.GetRequiredService<CallLog>().Entries);
        }
    }

    [Test]
    public async Task Parallel_inner_SkipTransaction_commands_are_rejected_and_transaction_is_rolled_back()
    {
        // Risco: sem transação própria, os dois usariam o IUnitOfWork (e o DbContext) do externo ao mesmo tempo
        var (_, exception, calls) = await RunAsync(ChildrenWithoutTransactionMode.Parallel);

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.Message).Contains("em paralelo a outro command interno na mesma transação");
        await Assert.That(calls).DoesNotContain("semtx:B");
        await Assert.That(calls).DoesNotContain("commit");
        await Assert.That(calls[^1]).IsEqualTo("rollback");
    }

    [Test]
    public async Task Sequential_inner_SkipTransaction_commands_are_still_allowed()
    {
        var (result, _, calls) = await RunAsync(ChildrenWithoutTransactionMode.Sequential);

        await Assert.That(result!.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "semtx:A", "semtx:B", "pai", "commit" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Transactional_command_inside_inner_SkipTransaction_command_is_still_allowed()
    {
        var (result, _, calls) = await RunAsync(ChildrenWithoutTransactionMode.WithTransactionalGrandchild);

        await Assert.That(result!.IsSuccess).IsTrue();
        await Assert.That(calls).IsEquivalentTo(new[] { "begin", "semtx:A", "filho:A.neto", "pai", "commit" }, CollectionOrdering.Matching);
    }
}

public class TransactionLostTests
{
    [Test]
    public async Task Inner_command_with_outer_transaction_ended_outside_pipeline_gets_clear_error()
    {
        // Risco: a mensagem de "Send em paralelo" esconderia a causa real (transação desfeita pelo banco ou IUnitOfWork não Scoped)
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () =>
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LosesTransactionCommand());
            })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("HasActiveTransaction = false");
        await Assert.That(ex.Message).DoesNotContain("em paralelo");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "rollback" }, CollectionOrdering.Matching);
    }
}

public class ExternalTransactionTests
{
    [Test]
    public async Task Command_without_PublishAfterCommit_joins_transaction_opened_outside_pipeline()
    {
        using var provider = TestHost.Build(withUnitOfWork: true);
        using var scope = provider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginTransactionAsync(CancellationToken.None);

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ChildCommand("A", 0));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(unitOfWork.HasActiveTransaction).IsTrue(); // quem abriu decide o commit
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "filho:A" }, CollectionOrdering.Matching);
    }
}

public class KeyedRegistrationTests
{
    [Test]
    public async Task Keyed_handler_registered_before_AddTecCqrs_does_not_prevent_handler_registration()
    {
        // Risco: ler ImplementationType de um registro keyed lança exceção sem relação com o problema
        var services = new ServiceCollection();
        services.AddKeyedScoped<IRequestHandler<PublishesQuery, Result<int>>, PublishesHandler>("v2");
        services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishesQuery());

        await Assert.That(result.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Keyed_authorizer_does_not_count_as_declared_authorization()
    {
        // Risco: passar na inicialização e falhar só em produção (o pipeline não resolve authorizers keyed)
        var services = new ServiceCollection();
        services.AddKeyedScoped<IRequestAuthorizer<NoAuthorizationQuery<int>>, DenyAuthorizer<NoAuthorizationQuery<int>>>("k");

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<NoAuthorizationQuery<int>, int, NoAuthorizationHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("sem autorização declarada");
        await Assert.That(ex.Message).Contains(typeof(NoAuthorizationQuery<int>).FullName!);
    }

    [Test]
    public async Task Keyed_validator_for_non_request_type_is_ignored()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IRequestValidator<IHasDocumentRequest>, RequiredDocumentValidator<IHasDocumentRequest>>("k");

        await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>())).ThrowsNothing();
    }
}

public class HandlerLifetimeTests
{
    [Test]
    public async Task Singleton_handler_registered_before_AddTecCqrs_fails_at_startup()
    {
        // Risco: o singleton capturaria DbContext/IUnitOfWork de um escopo e os compartilharia entre requisições
        var services = new ServiceCollection();
        services.AddSingleton<IRequestHandler<PublishesQuery, Result<int>>, PublishesHandler>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Singleton");
        await Assert.That(ex.Message).Contains(typeof(PublishesHandler).FullName!);
    }

    [Test]
    public async Task Same_scoped_handler_registered_before_AddTecCqrs_is_accepted()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestHandler<PublishesQuery, Result<int>>, PublishesHandler>();

        services.AddTecCqrs(o => o.AddQueryHandler<PublishesQuery, int, PublishesHandler>());

        await Assert.That(services.Count(d => d.ServiceType == typeof(IRequestHandler<PublishesQuery, Result<int>>))).IsEqualTo(1);
    }
}

public class OpenGenericAuthorizerTests
{
    [Test]
    public async Task Open_generic_authorizer_registered_before_counts_as_declared_authorization()
    {
        // Risco: a inicialização acusar todas as requisições, empurrando para [AllowAnonymousRequest] ou RequireAuthorization = false
        var services = new ServiceCollection();
        services.AddScoped(typeof(IRequestAuthorizer<>), typeof(DenyAuthorizer<>));
        services.AddTecCqrs(o => o.AddQueryHandler<NoAuthorizationQuery<int>, int, NoAuthorizationHandler<int>>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoAuthorizationQuery<int>());

        await Assert.That(result.Error!.Code).IsEqualTo("RECURSO_DE_OUTRO_CLIENTE"); // e é executado
    }

    [Test]
    public async Task Open_generic_authorizer_with_satisfied_constraint_counts_as_declared_authorization()
    {
        var services = new ServiceCollection();
        services.AddScoped<CallLog>();
        services.AddScoped(typeof(IRequestAuthorizer<>), typeof(TenantAuthorizer<>));
        services.AddTecCqrs(o => o.AddQueryHandler<TenantQuery<int>, int, TenantQueryHandler<int>>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new TenantQuery<int>());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "tenant" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Open_generic_authorizer_with_unsatisfied_constraint_does_not_count_as_authorization()
    {
        // O container não aplica o authorizer a quem não atende à restrição: contar seria fail-open
        var services = new ServiceCollection();
        services.AddScoped(typeof(IRequestAuthorizer<>), typeof(TenantAuthorizer<>));

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.AddQueryHandler<NoAuthorizationQuery<int>, int, NoAuthorizationHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(typeof(NoAuthorizationQuery<int>).FullName!);
    }

    [Test]
    public async Task Open_generic_authorizer_with_unsatisfied_constraint_does_not_break_execution()
    {
        var services = new ServiceCollection();
        services.AddScoped<CallLog>();
        services.AddScoped(typeof(IRequestAuthorizer<>), typeof(TenantAuthorizer<>));
        services.AddTecCqrs(o =>
        {
            o.RequireAuthorization = false;
            o.AddQueryHandler<NoAuthorizationQuery<int>, int, NoAuthorizationHandler<int>>();
        });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoAuthorizationQuery<int>());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).IsEmpty();
    }
}

public class RootScopeResolutionTests
{
    [Test]
    public async Task Parallel_Sends_with_mediator_resolved_from_root_provider_explain_the_cause()
    {
        // Resolvido da raiz (ValidateScopes desligado), o mediator e o IUnitOfWork são compartilhados pelo processo:
        // a rejeição está correta, e a mensagem precisa apontar a causa
        var collection = new ServiceCollection();
        collection.AddScoped<CallLog>();
        collection.AddScoped<FakeUnitOfWork>();
        collection.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FakeUnitOfWork>());
        collection.AddTecCqrs(o => o.AddCommandHandler<ParallelCommand, ParallelHandler>());
        using var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
        var sender = provider.GetRequiredService<ISender>();

        var ex = await Assert.That(async () =>
            {
                await Task.WhenAll(
                    sender.Send(new ParallelCommand("A", DelayMs: 50)),
                    sender.Send(new ParallelCommand("B", DelayMs: 0)));
            })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("em paralelo");
        await Assert.That(ex.Message).Contains("resolvido fora de um escopo");
    }
}
