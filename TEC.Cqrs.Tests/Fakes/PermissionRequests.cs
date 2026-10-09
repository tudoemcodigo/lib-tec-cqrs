using System.Security.Claims;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Permissões declarativas -----

[RequirePermission("pedidos:aprovar")]
[SkipValidation]
public sealed record ApprovePermissionCommand : ICommand;

internal sealed class ApprovePermissionHandler(CallLog log) : ICommandHandler<ApprovePermissionCommand>
{
    public Task<Result> Handle(ApprovePermissionCommand command, CancellationToken cancellationToken)
    {
        log.Add("approve");
        return Task.FromResult(Result.Success());
    }
}

[RequirePermission("ia:auditar", "relatorios:ver", Mode = PermissionMatch.Any)]
public sealed record AnyPermissionQuery : IQuery<int>;

internal sealed class AnyPermissionHandler : IQueryHandler<AnyPermissionQuery, int>
{
    public Task<Result<int>> Handle(AnyPermissionQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Duas regras: precisa de "a" e "b" (todas) e de "c" ou "d" (qualquer uma).</summary>
[RequirePermission("a", "b")]
[RequirePermission("c", "d", Mode = PermissionMatch.Any)]
public sealed record TwoRulesQuery : IQuery<int>;

internal sealed class TwoRulesHandler : IQueryHandler<TwoRulesQuery, int>
{
    public Task<Result<int>> Handle(TwoRulesQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Papel e permissão combinados (as duas regras valem).</summary>
[AuthorizeRequest(Roles = "Gestor")]
[RequirePermission("pedidos:aprovar")]
public sealed record RoleAndPermissionQuery : IQuery<int>;

internal sealed class RoleAndPermissionHandler : IQueryHandler<RoleAndPermissionQuery, int>
{
    public Task<Result<int>> Handle(RoleAndPermissionQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

// Inválidos: genéricos abertos não entram na varredura; os testes os registram explicitamente

[RequirePermission]
public sealed record NoPermissionsQuery<T> : IQuery<int>;

internal sealed class NoPermissionsHandler<T> : IQueryHandler<NoPermissionsQuery<T>, int>
{
    public Task<Result<int>> Handle(NoPermissionsQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

[RequirePermission("ok", " ")]
public sealed record BlankPermissionQuery<T> : IQuery<int>;

internal sealed class BlankPermissionHandler<T> : IQueryHandler<BlankPermissionQuery<T>, int>
{
    public Task<Result<int>> Handle(BlankPermissionQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

[AllowAnonymousRequest]
[RequirePermission("x")]
public sealed record AnonymousWithPermissionQuery<T> : IQuery<int>;

internal sealed class AnonymousWithPermissionHandler<T> : IQueryHandler<AnonymousWithPermissionQuery<T>, int>
{
    public Task<Result<int>> Handle(AnonymousWithPermissionQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

public static class PermissionPrincipals
{
    public static ClaimsPrincipal With(params string[] permissions) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ana"), .. permissions.Select(p => new Claim("tec_perm", p))], authenticationType: "Teste"));
}

/// <summary>Verificador próprio: concede tudo que começa com o prefixo.</summary>
public sealed class PrefixPermissionChecker(string prefix) : IPermissionChecker
{
    public int Calls { get; private set; }

    public ValueTask<bool> HasPermissionAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult(permission.StartsWith(prefix, StringComparison.Ordinal));
    }
}
