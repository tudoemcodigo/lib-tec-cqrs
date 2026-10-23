namespace TEC.Cqrs.Authorization;

/// <summary>Como as permissões de um <see cref="RequirePermissionAttribute"/> são combinadas.</summary>
public enum PermissionMatch
{
    /// <summary>O usuário precisa ter todas as permissões listadas (padrão).</summary>
    All = 0,

    /// <summary>Basta o usuário ter uma das permissões listadas.</summary>
    Any = 1
}

/// <summary>
/// Exige permissões para executar a requisição. Verificado no pipeline (antes da validação), valendo para qualquer ponto
/// de entrada: API, jobs, mensageria etc. Implica autenticação: sem usuário autenticado, falha <c>Unauthorized</c> (HTTP 401).
/// </summary>
/// <remarks>
/// <para>Cada atributo é uma regra; com vários atributos, todas as regras precisam ser atendidas. Dentro de uma regra,
/// <see cref="Mode"/> define se valem todas as permissões (<see cref="PermissionMatch.All"/>, padrão) ou qualquer uma
/// (<see cref="PermissionMatch.Any"/>).</para>
/// <para>A verificação é feita pelo <see cref="IPermissionChecker"/>. O padrão lê os claims do tipo
/// <c>CqrsOptions.PermissionClaimType</c> (<c>tec_perm</c>, o mesmo do TEC.Security); registre a sua implementação para
/// usar outra fonte (ex.: um cadastro de permissões por papel).</para>
/// <para>Sem permissões, com permissão em branco ou combinado com <see cref="AllowAnonymousRequestAttribute"/>, o
/// <c>AddTecCqrs</c> falha na inicialização (fail closed). Conta como autorização declarada para
/// <c>CqrsOptions.RequireAuthorization</c>, então dispensa <see cref="AuthorizeRequestAttribute"/>.</para>
/// <para>Sem permissão: falha <c>Forbidden</c> (HTTP 403, código <c>ACESSO_NEGADO</c>).</para>
/// </remarks>
/// <example>
/// <code>
/// [RequirePermission("pedidos:aprovar")]
/// public sealed record AprovarPedidoCommand(Guid PedidoId) : ICommand;
///
/// // Auditores OU quem vê relatórios
/// [RequirePermission("ia:auditar", "relatorios:ver", Mode = PermissionMatch.Any)]
/// public sealed record MetricasQuery(DateOnly De, DateOnly Ate) : IQuery&lt;MetricasDto&gt;;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : Attribute
{
    /// <summary>Cria a regra com as permissões exigidas.</summary>
    /// <param name="permissions">Permissões (ao menos uma, nenhuma em branco).</param>
    public RequirePermissionAttribute(params string[] permissions)
    {
        Permissions = permissions ?? [];
    }

    /// <summary>Permissões da regra.</summary>
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>Combinação das permissões: todas (padrão) ou qualquer uma.</summary>
    public PermissionMatch Mode { get; init; } = PermissionMatch.All;
}
