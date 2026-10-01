using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TEC.Cqrs.Authorization;

namespace TEC.Cqrs.AspNetCore;

/// <summary>Usuário da autorização do pipeline: o <see cref="HttpContext.User"/> da requisição HTTP atual.</summary>
/// <remarks>Registrado pelo <c>.AddAspNetCore()</c>, se nenhum <see cref="IPrincipalAccessor"/> tiver sido registrado antes.</remarks>
internal sealed class HttpContextPrincipalAccessor(IHttpContextAccessor httpContextAccessor) : IPrincipalAccessor
{
    public ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;
}
