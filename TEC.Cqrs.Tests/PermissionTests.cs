using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.Tests.Fakes;

namespace TEC.Cqrs.Tests;

public class PermissionTests
{
    private static async Task<(TResult Result, ServiceProvider Provider)> SendAsync<TResult>(IRequest<TResult> request, ClaimsPrincipal? user,
        Action<IServiceCollection>? services = null, Action<CqrsOptions>? configure = null)
        where TResult : Result
    {
        var provider = TestHost.Build(configure, services: s =>
        {
            s.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = user });
            services?.Invoke(s);
        });
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        return (result, provider);
    }

    [Test]
    public async Task Permission_present_in_claims_executes()
    {
        var (result, provider) = await SendAsync(new ApprovePermissionCommand(), PermissionPrincipals.With("pedidos:aprovar"));
        await using var _ = provider;

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Missing_permission_returns_Forbidden_ACESSO_NEGADO_without_running_handler()
    {
        await using var provider = TestHost.Build(services: s =>
            s.AddSingleton<IPrincipalAccessor>(new FakePrincipalAccessor { Principal = PermissionPrincipals.With("pedidos:ler") }));
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ApprovePermissionCommand());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Forbidden);
        await Assert.That(result.Error.Code).IsEqualTo(ForbiddenException.DefaultCode);
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).IsEmpty();
    }

    [Test]
    public async Task Permission_implies_authentication()
    {
        var (anonymous, p1) = await SendAsync(new ApprovePermissionCommand(), null);
        var (unauthenticated, p2) = await SendAsync(new ApprovePermissionCommand(),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tec_perm", "pedidos:aprovar")])));
        await using var _ = p1;
        await using var __ = p2;

        await Assert.That(anonymous.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
        await Assert.That(unauthenticated.Error!.Type).IsEqualTo(ErrorType.Unauthorized);
    }

    [Test]
    public async Task Permission_is_case_sensitive()
    {
        var (result, provider) = await SendAsync(new ApprovePermissionCommand(), PermissionPrincipals.With("Pedidos:Aprovar"));
        await using var _ = provider;

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.Forbidden);
    }

    [Test]
    [Arguments("ia:auditar", true)]
    [Arguments("relatorios:ver", true)]
    [Arguments("outra", false)]
    public async Task Any_mode_accepts_one_of_the_permissions(string permission, bool allowed)
    {
        var (result, provider) = await SendAsync(new AnyPermissionQuery(), PermissionPrincipals.With(permission));
        await using var _ = provider;

        await Assert.That(result.IsSuccess).IsEqualTo(allowed);
    }

    [Test]
    [Arguments(new[] { "a", "b", "c" }, true)]
    [Arguments(new[] { "a", "b", "d" }, true)]
    [Arguments(new[] { "a", "c" }, false)]
    [Arguments(new[] { "a", "b" }, false)]
    public async Task Every_rule_must_be_satisfied(string[] permissions, bool allowed)
    {
        var (result, provider) = await SendAsync(new TwoRulesQuery(), PermissionPrincipals.With(permissions));
        await using var _ = provider;

        await Assert.That(result.IsSuccess).IsEqualTo(allowed);
    }

    [Test]
    public async Task Roles_and_permissions_are_combined()
    {
        var withPermissionOnly = PermissionPrincipals.With("pedidos:aprovar");
        var withBoth = PermissionPrincipals.With("pedidos:aprovar");
        ((ClaimsIdentity)withBoth.Identity!).AddClaim(new Claim(ClaimTypes.Role, "Gestor"));

        var (denied, p1) = await SendAsync(new RoleAndPermissionQuery(), withPermissionOnly);
        var (allowed, p2) = await SendAsync(new RoleAndPermissionQuery(), withBoth);
        await using var _ = p1;
        await using var __ = p2;

        await Assert.That(denied.Error!.Type).IsEqualTo(ErrorType.Forbidden);
        await Assert.That(allowed.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Custom_claim_type_is_used_by_default_checker()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "pedidos:aprovar")], "Teste"));

        var (result, provider) = await SendAsync(new ApprovePermissionCommand(), user, configure: o => o.PermissionClaimType = "permissions");
        await using var _ = provider;

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Custom_checker_registered_after_AddTecCqrs_replaces_default()
    {
        var checker = new PrefixPermissionChecker("pedidos:");

        var (result, provider) = await SendAsync(new ApprovePermissionCommand(), PermissionPrincipals.With(),
            services: s => s.AddSingleton<IPermissionChecker>(checker));
        await using var _ = provider;

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(checker.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Any_mode_stops_at_first_granted_permission()
    {
        var checker = new PrefixPermissionChecker("ia:");

        var (result, provider) = await SendAsync(new AnyPermissionQuery(), PermissionPrincipals.With(),
            services: s => s.AddSingleton<IPermissionChecker>(checker));
        await using var _ = provider;

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(checker.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task RequirePermission_counts_as_declared_authorization()
    {
        var missing = CqrsDiagnostics.FindRequestsWithoutAuthorization(new ServiceCollection(), typeof(ApprovePermissionCommand).Assembly);

        await Assert.That(missing).DoesNotContain(typeof(ApprovePermissionCommand));
    }

    [Test]
    public async Task FindRequestsWithoutPermission_lists_requests_without_attribute()
    {
        var missing = CqrsDiagnostics.FindRequestsWithoutPermission(typeof(ApprovePermissionCommand).Assembly);

        await Assert.That(missing).Contains(typeof(AuthenticatedQuery));
        await Assert.That(missing).DoesNotContain(typeof(ApprovePermissionCommand));
        await Assert.That(missing).DoesNotContain(typeof(NoValidatorCommand)); // [AllowAnonymousRequest]
    }

    [Test]
    public async Task Attribute_without_permissions_fails_at_startup()
    {
        var ex = await Assert.That(() => new ServiceCollection().AddTecCqrs(o => o.AddQueryHandler<NoPermissionsQuery<int>, int, NoPermissionsHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("sem permissões");
    }

    [Test]
    public async Task Blank_permission_fails_at_scan_registration()
    {
        var ex = await Assert.That(() => new CqrsOptions().RegisterTypes([typeof(BlankPermissionHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("em branco");
    }

    [Test]
    public async Task AllowAnonymous_with_permission_fails_at_startup()
    {
        var ex = await Assert.That(() =>
                new ServiceCollection().AddTecCqrs(o => o.AddQueryHandler<AnonymousWithPermissionQuery<int>, int, AnonymousWithPermissionHandler<int>>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("[RequirePermission]");
    }

    [Test]
    public async Task Blank_permission_claim_type_is_rejected()
    {
        await Assert.That(() => new CqrsOptions().PermissionClaimType = " ").ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Default_checker_matches_only_exact_claim_values()
    {
        var checker = new ClaimPermissionChecker(new CqrsOptions());
        var user = PermissionPrincipals.With("a:b");

        await Assert.That(await checker.HasPermissionAsync(user, "a:b", CancellationToken.None)).IsTrue();
        await Assert.That(await checker.HasPermissionAsync(user, "a", CancellationToken.None)).IsFalse();
        await Assert.That(await checker.HasPermissionAsync(user, "a:b ", CancellationToken.None)).IsFalse();
    }
}
