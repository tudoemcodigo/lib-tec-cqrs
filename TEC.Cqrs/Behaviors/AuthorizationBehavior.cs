using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Internal;

namespace TEC.Cqrs.Behaviors;

/// <summary>
/// Autoriza a requisição antes da validação: primeiro os <see cref="AuthorizeRequestAttribute"/> (autenticação, papéis e
/// policies), depois os <see cref="RequirePermissionAttribute"/> (pelo <see cref="IPermissionChecker"/>) e por fim os
/// <see cref="IRequestAuthorizer{TRequest}"/> (sem atributo, exigem usuário autenticado:
/// <see cref="CqrsOptions.AuthorizersRequireAuthentication"/>, conferido antes de criar qualquer authorizer; com atributos,
/// os authorizers só são criados depois que os atributos autorizam). Negado, o pipeline retorna falha sem validar nem
/// chamar o handler, sem revelar regras de validação a quem não tem acesso.
/// </summary>
/// <remarks>
/// Executa os authorizers do tipo da requisição e também os registrados para as classes base e interfaces dela
/// (ex.: <c>IRequestAuthorizer&lt;IPedidoDoCliente&gt;</c>), nessa ordem. Todos precisam autorizar.
/// </remarks>
internal sealed class AuthorizationBehavior<TRequest, TResponse>(CqrsOptions options, CqrsRegistry registry,
    IServiceProvider serviceProvider, IServiceProviderIsService? isService = null)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private static readonly Error NotAuthenticated =
        Error.Unauthorized(UnauthenticatedException.DefaultCode, UnauthenticatedException.DefaultMessage);

    private static readonly Error AccessDenied =
        Error.Forbidden(ForbiddenException.DefaultCode, ForbiddenException.DefaultMessage);

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var rules = RequestInfo<TRequest>.AuthorizeRules;
        var permissionRules = RequestInfo<TRequest>.PermissionRules;
        bool hasAttributes = rules.Length > 0 || permissionRules.Length > 0;
        var targets = registry.GetAuthorizerTargets<TRequest>();

        // Container sem IServiceProviderIsService: só dá para saber se há authorizer resolvendo (comportamento anterior)
        var authorizers = isService is null ? ResolveAuthorizers(targets) : null;

        if (hasAttributes)
        {
            if (await AuthorizeAttributesAsync(request, rules, permissionRules, cancellationToken).ConfigureAwait(false) is { } denied)
                return ResultFactory<TResponse>.Failure([denied]);
        }
        else if (options.AuthorizersRequireAuthentication && !RequestInfo<TRequest>.AllowAnonymous
                 && (authorizers is not null ? authorizers.Length > 0 : HasRegisteredAuthorizer(targets))
                 && serviceProvider.GetService<IPrincipalAccessor>()?.Principal?.Identity?.IsAuthenticated != true)
        {
            // Só authorizers: o usuário autenticado é exigido antes de criar qualquer authorizer (as dependências deles,
            // como DbContext ou clientes HTTP, não são instanciadas para o anônimo), sem depender de cada um tratar o anônimo
            return ResultFactory<TResponse>.Failure([NotAuthenticated]);
        }

        authorizers ??= ResolveAuthorizers(targets);
        if (!hasAttributes && authorizers.Length == 0)
        {
            // Fail closed: com RequireAuthorization, toda requisição precisa declarar como é autorizada
            if (options.RequireAuthorization && !RequestInfo<TRequest>.AllowAnonymous)
            {
                throw new InvalidOperationException(
                    $"A requisição '{typeof(TRequest).FullName}' não declara autorização. Use [AuthorizeRequest] ou [RequirePermission], " +
                    "crie um IRequestAuthorizer ou marque com [AllowAnonymousRequest].");
            }

            return await next(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (target, authorizer) in authorizers)
        {
            var result = await target.Invoke(authorizer, request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"O authorizer '{authorizer.GetType().FullName}' retornou null.");

            if (result.IsFailure)
                return ResultFactory<TResponse>.Failure(result.Errors);
        }

        return await next(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Authorizers do tipo, das classes base e das interfaces da requisição, nessa ordem.</summary>
    /// <remarks>
    /// Sem alocação quando não há authorizer: os alvos vêm do cache por tipo do <see cref="CqrsRegistry"/> e só são
    /// resolvidos os que o container conhece (<see cref="IServiceProviderIsService"/>). A presença de authorizers não fica
    /// em cache por tipo: os registros podem variar entre containers, e um authorizer ignorado seria fail-open.
    /// </remarks>
    private (AuthorizerTarget Target, object Authorizer)[] ResolveAuthorizers(AuthorizerTarget[] targets)
    {
        List<(AuthorizerTarget, object)>? resolved = null;
        foreach (var target in targets)
        {
            // Sem IServiceProviderIsService (container de terceiros), resolve sempre
            if (isService is not null && !isService.IsService(target.ServiceType))
                continue;

            foreach (var authorizer in target.Resolve(serviceProvider))
            {
                if (authorizer is not null)
                    (resolved ??= []).Add((target, authorizer));
            }
        }

        return resolved is null ? [] : [.. resolved];
    }

    /// <summary>
    /// Há authorizer que se aplica a algum dos alvos (requer <c>isService</c>). Os registrados pelo <c>AddTecCqrs</c> são
    /// confirmados sem instanciá-los.
    /// </summary>
    /// <remarks>
    /// Registro direto no container (fechado ou genérico aberto) é confirmado resolvendo: o <c>IsService</c> do container
    /// da Microsoft responde <c>true</c> para um genérico aberto mesmo quando a restrição do parâmetro recusa a requisição,
    /// e nesse caso o <c>GetServices</c> não devolve nenhum authorizer.
    /// </remarks>
    private bool HasRegisteredAuthorizer(AuthorizerTarget[] targets)
    {
        foreach (var target in targets)
        {
            if (!isService!.IsService(target.ServiceType))
                continue;

            if (registry.IsDeclaredAuthorizer(target.TargetType))
                return true;

            foreach (var authorizer in target.Resolve(serviceProvider))
            {
                if (authorizer is not null)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Retorna o erro de negação, ou <c>null</c> se todas as regras forem atendidas.</summary>
    private async Task<Error?> AuthorizeAttributesAsync(TRequest request, AuthorizeRule[] rules, PermissionRule[] permissionRules,
        CancellationToken cancellationToken)
    {
        var principal = serviceProvider.GetService<IPrincipalAccessor>()?.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
            return NotAuthenticated;

        foreach (var rule in rules)
        {
            if (!IsInAnyRole(principal, rule.Roles))
                return AccessDenied;

            if (rule.Policy is not null)
            {
                var evaluator = serviceProvider.GetService<IRequestPolicyEvaluator>()
                    ?? throw new InvalidOperationException(
                        $"A requisição '{typeof(TRequest).FullName}' usa a policy '{rule.Policy}', mas nenhum IRequestPolicyEvaluator " +
                        "está registrado. Em ASP.NET Core, chame .AddAspNetCore() (pacote TEC.Cqrs.AspNetCore) e " +
                        "services.AddAuthorization(...); fora dele, registre a sua implementação de IRequestPolicyEvaluator.");

                if (!await evaluator.AuthorizeAsync(principal, request, rule.Policy, cancellationToken).ConfigureAwait(false))
                    return AccessDenied;
            }
        }

        if (permissionRules.Length == 0)
            return null;

        var checker = serviceProvider.GetService<IPermissionChecker>()
            ?? throw new InvalidOperationException(
                $"A requisição '{typeof(TRequest).FullName}' usa [RequirePermission], mas nenhum IPermissionChecker está registrado.");

        foreach (var rule in permissionRules)
        {
            if (!await SatisfiesAsync(checker, principal, rule, cancellationToken).ConfigureAwait(false))
                return AccessDenied;
        }

        return null;
    }

    private static async ValueTask<bool> SatisfiesAsync(IPermissionChecker checker, ClaimsPrincipal principal, PermissionRule rule,
        CancellationToken cancellationToken)
    {
        bool any = rule.Match == PermissionMatch.Any;
        foreach (var permission in rule.Permissions)
        {
            bool has = await checker.HasPermissionAsync(principal, permission, cancellationToken).ConfigureAwait(false);
            if (any && has)
                return true;
            if (!any && !has)
                return false;
        }

        // All: todas atendidas; Any: nenhuma atendida
        return !any;
    }

    private static bool IsInAnyRole(ClaimsPrincipal principal, string[] roles)
    {
        if (roles.Length == 0)
            return true;

        // Uma identidade anexada sem autenticação não concede papéis. Com todas autenticadas (o caso comum), usa o IsInRole
        // do principal, que pode ser especializado (ex.: WindowsPrincipal, por nome de grupo)
        if (principal.Identities.All(identity => identity.IsAuthenticated))
            return roles.Any(principal.IsInRole);

        foreach (var identity in principal.Identities)
        {
            if (!identity.IsAuthenticated)
                continue;

            foreach (var role in roles)
            {
                if (identity.HasClaim(identity.RoleClaimType, role))
                    return true;
            }
        }

        return false;
    }
}
