using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Authorization;

namespace TEC.Cqrs.AspNetCore;

/// <summary>
/// Avalia as policies de <see cref="AuthorizeRequestAttribute.Policy"/> com o <see cref="IAuthorizationService"/> do
/// ASP.NET Core, passando a própria requisição como recurso.
/// </summary>
/// <remarks>Registrado pelo <c>.AddAspNetCore()</c>; as policies vêm de <c>services.AddAuthorization(...)</c>.</remarks>
internal sealed class AuthorizationServicePolicyEvaluator(IServiceProvider serviceProvider) : IRequestPolicyEvaluator
{
    public async Task<bool> AuthorizeAsync(ClaimsPrincipal user, object request, string policy, CancellationToken cancellationToken)
    {
        var authorizationService = serviceProvider.GetService<IAuthorizationService>()
            ?? throw new InvalidOperationException(
                $"A requisição '{request.GetType().FullName}' usa a policy '{policy}', mas o IAuthorizationService " +
                "não está registrado. Chame services.AddAuthorization(...).");

        var result = await authorizationService.AuthorizeAsync(user, request, policy).ConfigureAwait(false);
        return result.Succeeded;
    }
}
