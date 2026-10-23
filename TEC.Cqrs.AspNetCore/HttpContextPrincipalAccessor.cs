using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TEC.Cqrs.Authorization;

namespace TEC.Cqrs.AspNetCore;

/// <summary>Usuário da autorização do pipeline: o <see cref="HttpContext.User"/> da requisição HTTP atual.</summary>
/// <remarks>
/// Registrado pelo <c>.AddAspNetCore()</c>. Um <see cref="IPrincipalAccessor"/> registrado antes faz a chamada falhar, salvo
/// com <see cref="CqrsAspNetCoreOptions.ReplaceExistingPrincipalAccessor"/>.
/// </remarks>
internal sealed class HttpContextPrincipalAccessor(IHttpContextAccessor httpContextAccessor) : IPrincipalAccessor
{
    public ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;
}
