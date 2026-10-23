using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TEC.Cqrs.SampleApi;

/// <summary>
/// Autenticação <b>somente de exemplo</b>: o usuário vem do cabeçalho <c>X-Usuario</c> e os papéis de <c>X-Papeis</c>,
/// para o gerador de carga simular muitos usuários sem um provedor de identidade. Nunca use em produção: qualquer
/// cliente se declara quem quiser. Em uma API real, use JWT/OIDC (<c>AddJwtBearer</c>) ou o TEC.Security.
/// </summary>
public sealed class SampleAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Exemplo";
    public const string UserHeader = "X-Usuario";
    public const string RolesHeader = "X-Papeis";
    public const string CustomerClaim = "cliente_id";

    private static readonly string[] KnownRoles = [SampleApiApp.CustomerRole, SampleApiApp.AdminRole];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var values) || values.Count == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        // Identificador restrito (letras minúsculas, dígitos e hífen): nada de valores enormes ou caracteres de controle
        string user = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        if (user.Length is 0 or > 64 || !user.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            return Task.FromResult(AuthenticateResult.Fail("Usuário inválido."));

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, user), new(ClaimTypes.Name, user), new(CustomerClaim, user)];
        foreach (var role in Request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Array.IndexOf(KnownRoles, role) < 0)
                return Task.FromResult(AuthenticateResult.Fail("Papel inválido."));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
