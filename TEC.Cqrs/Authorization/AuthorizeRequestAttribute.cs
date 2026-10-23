namespace TEC.Cqrs.Authorization;

/// <summary>
/// Exige usuário autenticado para executar a requisição e, opcionalmente, uma policy e/ou papéis.
/// Verificado no pipeline (antes da validação), valendo para qualquer ponto de entrada: API, jobs, mensageria etc.
/// </summary>
/// <remarks>
/// <para>Sem <see cref="Policy"/> e sem <see cref="Roles"/>, exige apenas autenticação. Informados em branco
/// (<c>Roles = ","</c>, <c>Policy = " "</c>), a inicialização falha com <see cref="InvalidOperationException"/>, em vez de
/// a regra degradar para "qualquer usuário autenticado".</para>
/// <para><see cref="Policy"/> é avaliada pelo <see cref="IRequestPolicyEvaluator"/> e recebe a própria requisição como
/// recurso, permitindo regras baseadas no conteúdo. Em ASP.NET Core, use o <c>.AddAspNetCore()</c> do pacote
/// <c>TEC.Cqrs.AspNetCore</c> (que usa o <c>IAuthorizationService</c>) e registre as policies com
/// <c>services.AddAuthorization(...)</c>.</para>
/// <para>Com vários atributos, todos precisam ser atendidos. O usuário vem do <see cref="IPrincipalAccessor"/>.</para>
/// <para>Não autenticado: falha <c>Unauthorized</c> (HTTP 401). Sem permissão: falha <c>Forbidden</c> (HTTP 403).</para>
/// </remarks>
/// <example>
/// <code>
/// [AuthorizeRequest(Policy = "ClientesEscrita")]
/// public sealed record CriarClienteCommand(string Nome, string Cpf) : ICommand&lt;Guid&gt;;
///
/// [AuthorizeRequest(Roles = "Admin,Financeiro")]
/// public sealed record EstornarPagamentoCommand(Guid PagamentoId) : ICommand;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = true)]
public sealed class AuthorizeRequestAttribute : Attribute
{
    /// <summary>
    /// Nome da policy de autorização (opcional), avaliada pelo <see cref="IRequestPolicyEvaluator"/>. Não pode ser vazio
    /// nem só espaços.
    /// </summary>
    public string? Policy { get; init; }

    /// <summary>
    /// Papéis aceitos, separados por vírgula; basta o usuário ter um deles (opcional). Se informado, precisa ter ao menos
    /// um papel.
    /// </summary>
    public string? Roles { get; init; }
}

/// <summary>
/// Declara que a requisição pode ser executada sem autenticação. Com <c>CqrsOptions.RequireAuthorization</c> (padrão),
/// toda requisição sem <see cref="AuthorizeRequestAttribute"/> nem <see cref="IRequestAuthorizer{TRequest}"/> precisa deste
/// atributo, para deixar explícito que é pública; sem ele, o <c>AddTecCqrs</c> falha na inicialização.
/// </summary>
/// <remarks>
/// <para>Não pode ser combinado com <see cref="AuthorizeRequestAttribute"/> (erro na inicialização).</para>
/// <para>Use também em requisições disparadas apenas por jobs ou mensageria sem usuário (ou registre um
/// <see cref="IPrincipalAccessor"/> com a identidade do sistema).</para>
/// </remarks>
/// <example>
/// <code>
/// [AllowAnonymousRequest]
/// public sealed record ListarPlanosPublicosQuery : IQuery&lt;IReadOnlyList&lt;PlanoDto&gt;&gt;;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class AllowAnonymousRequestAttribute : Attribute
{
}
