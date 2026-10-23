using System.Reflection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Internal;

/// <summary>Metadados de cada tipo de requisição, calculados uma única vez.</summary>
internal static class RequestInfo<TRequest>
{
    public static readonly string Name = typeof(TRequest).Name;

    public static readonly string Kind = RequestMetadata.IsCommand(typeof(TRequest)) ? "command"
        : typeof(IQueryBase).IsAssignableFrom(typeof(TRequest)) ? "query"
        : "request";

    public static readonly bool IsCommand = RequestMetadata.IsCommand(typeof(TRequest));

    public static readonly bool IsTransactional = RequestMetadata.IsTransactional(typeof(TRequest));

    public static readonly bool RequiresValidator = RequestMetadata.RequiresValidator(typeof(TRequest));

    /// <summary>Regras dos <see cref="AuthorizeRequestAttribute"/>, com os papéis já separados (sem alocação por requisição).</summary>
    public static readonly AuthorizeRule[] AuthorizeRules =
        [.. RequestMetadata.GetAuthorizeAttributes(typeof(TRequest)).Select(a => AuthorizeRule.From(typeof(TRequest), a))];

    public static readonly bool AllowAnonymous = RequestMetadata.Has<AllowAnonymousRequestAttribute>(typeof(TRequest));

    /// <summary>Regras dos <see cref="RequirePermissionAttribute"/> (validadas; sem reflexão por requisição).</summary>
    public static readonly PermissionRule[] PermissionRules =
        [.. RequestMetadata.GetPermissionAttributes(typeof(TRequest)).Select(a => PermissionRule.From(typeof(TRequest), a))];
}

/// <summary>Um <see cref="RequirePermissionAttribute"/> pré-processado.</summary>
/// <param name="Permissions">Permissões da regra (ao menos uma, sem repetição).</param>
/// <param name="Match">Todas ou qualquer uma.</param>
internal sealed record PermissionRule(string[] Permissions, PermissionMatch Match)
{
    /// <exception cref="InvalidOperationException">
    /// Atributo sem permissões, com permissão em branco ou com <c>Match</c> inválido (a regra seria ignorada ou degradaria
    /// para "qualquer usuário autenticado").
    /// </exception>
    public static PermissionRule From(Type requestType, RequirePermissionAttribute attribute)
    {
        if (attribute.Permissions.Count == 0)
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [RequirePermission] sem permissões. Informe ao menos uma permissão.");
        }

        if (attribute.Permissions.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [RequirePermission] com permissão em branco.");
        }

        if (!Enum.IsDefined(attribute.Mode))
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [RequirePermission] com Mode inválido ({(int)attribute.Mode}).");
        }

        return new([.. attribute.Permissions.Distinct(StringComparer.Ordinal)], attribute.Mode);
    }
}

/// <summary>Um <see cref="AuthorizeRequestAttribute"/> pré-processado.</summary>
/// <param name="Policy">Policy (avaliada pelo <see cref="IRequestPolicyEvaluator"/>), ou <c>null</c>.</param>
/// <param name="Roles">Papéis aceitos (vazio: qualquer usuário autenticado).</param>
internal sealed record AuthorizeRule(string? Policy, string[] Roles)
{
    /// <exception cref="InvalidOperationException">
    /// <c>Roles</c> informado sem nenhum papel (ex.: <c>","</c>) ou <c>Policy</c> em branco: a regra degradaria para
    /// "qualquer usuário autenticado" (fail closed).
    /// </exception>
    public static AuthorizeRule From(Type requestType, AuthorizeRequestAttribute attribute)
    {
        if (attribute.Policy is not null && string.IsNullOrWhiteSpace(attribute.Policy))
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [AuthorizeRequest] com Policy em branco. " +
                "Informe o nome da policy ou remova a propriedade (em branco, a requisição exigiria apenas autenticação).");
        }

        string[] roles = attribute.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (attribute.Roles is not null && roles.Length == 0)
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [AuthorizeRequest] com Roles sem nenhum papel ('{attribute.Roles}'). " +
                "Informe ao menos um papel ou remova a propriedade (em branco, a requisição exigiria apenas autenticação).");
        }

        return new(attribute.Policy, roles);
    }
}

/// <summary>Regras sobre tipos de requisição, compartilhadas pelo pipeline, pelo registro e pelos diagnósticos.</summary>
internal static class RequestMetadata
{
    public static bool IsCommand(Type requestType) => typeof(ICommandBase).IsAssignableFrom(requestType);

    /// <summary>Command que participa de transação (sem <see cref="SkipTransactionAttribute"/>).</summary>
    public static bool IsTransactional(Type requestType) => IsCommand(requestType) && !Has<SkipTransactionAttribute>(requestType);

    public static bool Has<TAttribute>(Type requestType) where TAttribute : Attribute =>
        requestType.GetCustomAttribute<TAttribute>(inherit: true) is not null;

    /// <summary>Command sem <see cref="SkipValidationAttribute"/> (vale quando <c>RequireValidatorForCommands</c> está ativo).</summary>
    public static bool RequiresValidator(Type requestType) => IsCommand(requestType) && !Has<SkipValidationAttribute>(requestType);

    public static AuthorizeRequestAttribute[] GetAuthorizeAttributes(Type requestType) =>
        [.. requestType.GetCustomAttributes<AuthorizeRequestAttribute>(inherit: true)];

    public static RequirePermissionAttribute[] GetPermissionAttributes(Type requestType) =>
        [.. requestType.GetCustomAttributes<RequirePermissionAttribute>(inherit: true)];

    /// <summary>
    /// A requisição declara como é autorizada: <see cref="AuthorizeRequestAttribute"/>, <see cref="RequirePermissionAttribute"/>, <see cref="AllowAnonymousRequestAttribute"/>
    /// ou um <see cref="IRequestAuthorizer{TRequest}"/> registrado (inclusive para tipo base ou interface, ou genérico aberto).
    /// </summary>
    /// <param name="requestType">Tipo da requisição.</param>
    /// <param name="authorizers">Os <c>IRequestAuthorizer</c> registrados no container.</param>
    public static bool DeclaresAuthorization(Type requestType, RegisteredAuthorizers authorizers) =>
        GetAuthorizeAttributes(requestType).Length > 0
        || GetPermissionAttributes(requestType).Length > 0
        || Has<AllowAnonymousRequestAttribute>(requestType)
        || authorizers.Authorizes(requestType);

    /// <summary>
    /// Falha na inicialização se o tipo de requisição tiver marcações contraditórias ou <c>[AuthorizeRequest]</c> com
    /// <c>Roles</c>/<c>Policy</c> em branco ou <c>[RequirePermission]</c> sem permissões.
    /// </summary>
    public static void EnsureValid(Type requestType)
    {
        // Sem marcador, a requisição escaparia da transação e da exigência de validator
        if (!IsCommand(requestType) && !typeof(IQueryBase).IsAssignableFrom(requestType))
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' deve implementar ICommand, ICommand<T> ou IQuery<T> (não IRequest<T> diretamente).");
        }

        if (IsCommand(requestType) && typeof(IQueryBase).IsAssignableFrom(requestType))
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' é ao mesmo tempo command e query. Implemente apenas ICommand ou IQuery.");
        }

        if (Has<AllowAnonymousRequestAttribute>(requestType) && GetAuthorizeAttributes(requestType).Length > 0)
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [AllowAnonymousRequest] e [AuthorizeRequest]. Use apenas um deles.");
        }

        if (Has<AllowAnonymousRequestAttribute>(requestType) && GetPermissionAttributes(requestType).Length > 0)
        {
            throw new InvalidOperationException(
                $"A requisição '{requestType.FullName}' possui [AllowAnonymousRequest] e [RequirePermission]. Use apenas um deles.");
        }

        foreach (var attribute in GetPermissionAttributes(requestType))
            _ = PermissionRule.From(requestType, attribute);

        // Roles/Policy em branco: falha aqui, e não na primeira execução (montagem do RequestInfo)
        foreach (var attribute in GetAuthorizeAttributes(requestType))
            _ = AuthorizeRule.From(requestType, attribute);
    }
}
