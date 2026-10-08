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
/// policies), depois os <see cref="IRequestAuthorizer{TRequest}"/>. Negado, o pipeline retorna falha sem validar nem
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
        var authorizers = ResolveAuthorizers();

        if (rules.Length == 0 && authorizers.Length == 0)
        {
            // Fail closed: com RequireAuthorization, toda requisição precisa declarar como é autorizada
            if (options.RequireAuthorization && !RequestInfo<TRequest>.AllowAnonymous)
            {
                throw new InvalidOperationException(
                    $"A requisição '{typeof(TRequest).FullName}' não declara autorização. Use [AuthorizeRequest], " +
                    "crie um IRequestAuthorizer ou marque com [AllowAnonymousRequest].");
            }

            return await next(cancellationToken).ConfigureAwait(false);
        }

        if (rules.Length > 0
            && await AuthorizeAttributesAsync(request, rules, cancellationToken).ConfigureAwait(false) is { } denied)
        {
            return ResultFactory<TResponse>.Failure([denied]);
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
    private (AuthorizerTarget Target, object Authorizer)[] ResolveAuthorizers()
    {
        List<(AuthorizerTarget, object)>? resolved = null;
        foreach (var target in registry.GetAuthorizerTargets<TRequest>())
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

    /// <summary>Retorna o erro de negação, ou <c>null</c> se todas as regras forem atendidas.</summary>
    private async Task<Error?> AuthorizeAttributesAsync(TRequest request, AuthorizeRule[] rules, CancellationToken cancellationToken)
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

        return null;
    }

    private static bool IsInAnyRole(ClaimsPrincipal principal, string[] roles)
    {
        if (roles.Length == 0)
            return true;

        foreach (var role in roles)
        {
            if (principal.IsInRole(role))
                return true;
        }

        return false;
    }
}
