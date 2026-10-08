using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;

namespace TEC.Cqrs.Authorization;

/// <summary>
/// Regra de autorização de uma requisição, executada antes da validação e do handler. Use para regras que dependem
/// do conteúdo da requisição (ex.: "o pedido pertence ao cliente logado?"). Registrada pela varredura de assemblies do
/// <c>AddTecCqrs</c> ou por <c>CqrsOptions.AddRequestAuthorizer</c> (Scoped).
/// </summary>
/// <remarks>
/// <para>Retorne <see cref="Result.Success()"/> para autorizar, ou falha com <c>Error.Forbidden</c> / <c>Error.Unauthorized</c>.
/// Para não revelar a existência do recurso a quem não tem acesso, retornar <c>Error.NotFound</c> também é válido.</para>
/// <para>Roda <b>antes</b> da validação: a requisição ainda não foi validada, então trate valores nulos ou inválidos.</para>
/// <para>Com vários authorizers para a mesma requisição, todos precisam autorizar (o primeiro que falhar interrompe).</para>
/// <para>Um authorizer de tipo base ou interface (ex.: <c>IRequestAuthorizer&lt;IPedidoDoCliente&gt;</c>, com
/// <c>IPedidoDoCliente : IBaseRequest</c>) vale para todas as requisições que o implementam. Ordem: os do tipo exato, os das
/// classes base e os das interfaces. Authorizers de tipo base ou interface só são encontrados se registrados pelo
/// <c>AddTecCqrs</c> (varredura ou <c>AddRequestAuthorizer</c>); os do tipo exato também valem se registrados direto no
/// container. Um authorizer de tipo base ou interface (ou de tipo que não é uma requisição conhecida) registrado direto
/// no container antes do <c>AddTecCqrs</c> faz a inicialização falhar com <see cref="InvalidOperationException"/>, em vez
/// de ficar sem executar. Registros feitos no container <b>depois</b> do <c>AddTecCqrs</c> não são verificados.</para>
/// </remarks>
/// <example>
/// <code>
/// internal sealed class CancelarPedidoAuthorizer(IPrincipalAccessor usuario, IPedidoRepository pedidos)
///     : IRequestAuthorizer&lt;CancelarPedidoCommand&gt;
/// {
///     public async Task&lt;Result&gt; AuthorizeAsync(CancelarPedidoCommand command, CancellationToken cancellationToken)
///     {
///         var clienteId = usuario.Principal?.FindFirst("cliente_id")?.Value;
///         return await pedidos.PertenceAoClienteAsync(command.PedidoId, clienteId, cancellationToken)
///             ? Result.Success()
///             : Error.NotFound("PEDIDO_NAO_ENCONTRADO", "Pedido não encontrado.");
///     }
/// }
/// </code>
/// </example>
public interface IRequestAuthorizer<in TRequest>
    where TRequest : IBaseRequest
{
    /// <summary>Autoriza (sucesso) ou nega (falha) a execução da requisição.</summary>
    Task<Result> AuthorizeAsync(TRequest request, CancellationToken cancellationToken);
}
