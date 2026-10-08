using System.Security.Claims;

namespace TEC.Cqrs.Authorization;

/// <summary>
/// Avalia as policies nomeadas de <see cref="AuthorizeRequestAttribute.Policy"/>. O núcleo não depende do ASP.NET Core:
/// o pacote <c>TEC.Cqrs.AspNetCore</c> (<c>.AddAspNetCore()</c>) registra uma implementação que usa o
/// <c>IAuthorizationService</c>; fora dele (workers, mensageria), registre a sua.
/// </summary>
/// <remarks>
/// Sem implementação registrada, uma requisição com policy lança <see cref="InvalidOperationException"/> ao ser executada
/// (fail closed). Papéis (<see cref="AuthorizeRequestAttribute.Roles"/>) não dependem desta interface.
/// </remarks>
/// <example>
/// Worker com <c>Microsoft.AspNetCore.Authorization</c> (pacote NuGet avulso):
/// <code>
/// internal sealed class PolicyEvaluator(IAuthorizationService authorization) : IRequestPolicyEvaluator
/// {
///     public async Task&lt;bool&gt; AuthorizeAsync(ClaimsPrincipal user, object request, string policy, CancellationToken cancellationToken) =&gt;
///         (await authorization.AuthorizeAsync(user, request, policy)).Succeeded;
/// }
///
/// services.AddScoped&lt;IRequestPolicyEvaluator, PolicyEvaluator&gt;();
/// </code>
/// </example>
public interface IRequestPolicyEvaluator
{
    /// <summary>Indica se o usuário atende à policy, recebendo a própria requisição como recurso.</summary>
    /// <param name="user">Usuário autenticado.</param>
    /// <param name="request">Requisição (recurso da autorização).</param>
    /// <param name="policy">Nome da policy.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    Task<bool> AuthorizeAsync(ClaimsPrincipal user, object request, string policy, CancellationToken cancellationToken);
}
