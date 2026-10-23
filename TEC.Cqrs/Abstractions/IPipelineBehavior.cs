using TEC.Core.Common.Results;

namespace TEC.Cqrs.Abstractions;

/// <summary>Próximo passo do pipeline (outro behavior ou o handler).</summary>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>(CancellationToken cancellationToken)
    where TResponse : Result;

/// <summary>
/// Comportamento transversal executado em volta do handler (logging, validação, transação etc.).
/// </summary>
/// <remarks>
/// Para interromper o fluxo sem exceção, retorne uma falha, por exemplo com
/// <c>Result.Failure(...)</c> quando <typeparamref name="TResponse"/> for conhecido.
/// Behaviors próprios são registrados com <c>options.AddBehavior(typeof(MeuBehavior&lt;,&gt;))</c>.
/// </remarks>
/// <example>
/// <code>
/// internal sealed class AuditoriaBehavior&lt;TRequest, TResponse&gt;(IAuditoria auditoria)
///     : IPipelineBehavior&lt;TRequest, TResponse&gt;
///     where TRequest : IRequest&lt;TResponse&gt;
///     where TResponse : Result
/// {
///     public async Task&lt;TResponse&gt; Handle(TRequest request, RequestHandlerDelegate&lt;TResponse&gt; next, CancellationToken cancellationToken)
///     {
///         var response = await next(cancellationToken);
///         await auditoria.RegistrarAsync(typeof(TRequest).Name, response.IsSuccess, cancellationToken);
///         return response;
///     }
/// }
/// </code>
/// </example>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    /// <summary>Executa o comportamento e, se for o caso, chama <paramref name="next"/>.</summary>
    Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}
