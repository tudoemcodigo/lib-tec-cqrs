using Microsoft.AspNetCore.Builder;

namespace TEC.Cqrs.AspNetCore.Idempotency;

/// <summary>
/// Marca um endpoint como idempotente pelo cabeçalho <c>Idempotency-Key</c>: a mesma chave (do mesmo usuário) com o mesmo
/// conteúdo devolve a resposta original sem executar de novo. Só endpoints marcados são tratados pelo <c>UseTecIdempotency</c>.
/// </summary>
/// <remarks>Em Minimal APIs, prefira <see cref="IdempotencyEndpointConventionBuilderExtensions.WithIdempotency{TBuilder}"/>.</remarks>
/// <example>
/// <code>
/// [HttpPost, Idempotent(KeyRequired = true)]
/// public Task&lt;IActionResult&gt; Criar(CriarPedidoCommand command) => ...;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class IdempotentAttribute : Attribute
{
    /// <summary>Exige o cabeçalho: sem ele, HTTP 400 <c>IDEMPOTENCY_KEY_OBRIGATORIA</c>. Padrão: <c>false</c> (opcional).</summary>
    public bool KeyRequired { get; init; }
}

/// <summary>Marcação de endpoints idempotentes.</summary>
public static class IdempotencyEndpointConventionBuilderExtensions
{
    /// <summary>Marca o endpoint (ou grupo) como idempotente pelo cabeçalho <c>Idempotency-Key</c>.</summary>
    /// <typeparam name="TBuilder">Tipo do builder.</typeparam>
    /// <param name="builder">Endpoint ou grupo.</param>
    /// <param name="keyRequired">Exige o cabeçalho (sem ele, HTTP 400).</param>
    /// <returns>O próprio builder.</returns>
    /// <example>
    /// <code>
    /// app.MapPost("/pedidos", (CriarPedidoCommand c, ISender s, CancellationToken ct) => s.Send(c, ct).ToHttpResult())
    ///    .WithIdempotency();
    /// </code>
    /// </example>
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder, bool keyRequired = false)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new IdempotentAttribute { KeyRequired = keyRequired }));
        return builder;
    }
}
