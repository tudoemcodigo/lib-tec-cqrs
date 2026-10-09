using System.Security.Claims;
using TEC.Cqrs.DependencyInjection;

namespace TEC.Cqrs.Authorization;

/// <summary>
/// Verifica se o usuário tem uma permissão, para as regras de <see cref="RequirePermissionAttribute"/>.
/// </summary>
/// <remarks>
/// <para>O <c>AddTecCqrs</c> registra o <see cref="ClaimPermissionChecker"/>, que lê os claims do tipo
/// <see cref="CqrsOptions.PermissionClaimType"/>. Para usar outra fonte, registre a sua implementação depois do
/// <c>AddTecCqrs</c> (o último registro vale). É chamada só para usuários autenticados.</para>
/// <para>Fail closed: uma exceção da implementação sobe pelo pipeline (a requisição não é executada).</para>
/// </remarks>
/// <example>
/// <code>
/// internal sealed class PermissoesDoUsuario(ISecurityUser usuario) : IPermissionChecker
/// {
///     public ValueTask&lt;bool&gt; HasPermissionAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken) =>
///         ValueTask.FromResult(usuario.HasPermission(permission));
/// }
///
/// services.AddTecCqrs(...);
/// services.AddScoped&lt;IPermissionChecker, PermissoesDoUsuario&gt;();
/// </code>
/// </example>
public interface IPermissionChecker
{
    /// <summary>Indica se <paramref name="principal"/> tem <paramref name="permission"/>.</summary>
    /// <param name="principal">Usuário autenticado.</param>
    /// <param name="permission">Permissão exigida.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns><c>true</c> se o usuário tem a permissão.</returns>
    ValueTask<bool> HasPermissionAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IPermissionChecker"/> padrão: o usuário tem a permissão se tiver um claim do tipo
/// <see cref="CqrsOptions.PermissionClaimType"/> com o mesmo valor (comparação ordinal, sensível a maiúsculas).
/// </summary>
public sealed class ClaimPermissionChecker : IPermissionChecker
{
    private readonly string _claimType;

    /// <summary>Cria o verificador com o tipo de claim das opções.</summary>
    /// <param name="options">Opções do CQRS.</param>
    public ClaimPermissionChecker(CqrsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _claimType = options.PermissionClaimType;
    }

    /// <inheritdoc />
    public ValueTask<bool> HasPermissionAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        foreach (var claim in principal.FindAll(_claimType))
        {
            if (string.Equals(claim.Value, permission, StringComparison.Ordinal))
                return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }
}
