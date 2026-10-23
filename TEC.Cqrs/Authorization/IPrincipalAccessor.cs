using System.Security.Claims;

namespace TEC.Cqrs.Authorization;

/// <summary>
/// Fornece o usuário da requisição atual para a autorização do pipeline.
/// </summary>
/// <remarks>
/// <para>Em ASP.NET Core, o <c>.AddAspNetCore()</c> do pacote <c>TEC.Cqrs.AspNetCore</c> registra a implementação
/// que usa o <c>HttpContext.User</c>. Fora de requisições HTTP (jobs, mensageria), registre a sua implementação.</para>
/// <para>Sem implementação registrada, não há usuário: requisições com <see cref="AuthorizeRequestAttribute"/> retornam
/// falha <c>Unauthorized</c> (HTTP 401).</para>
/// </remarks>
/// <example>
/// <code>
/// services.AddScoped&lt;IPrincipalAccessor, UsuarioDaMensagemAccessor&gt;();
/// </code>
/// </example>
public interface IPrincipalAccessor
{
    /// <summary>Usuário atual, ou <c>null</c> se não houver.</summary>
    ClaimsPrincipal? Principal { get; }
}
