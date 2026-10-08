using TEC.Core.Common.Results;

namespace TEC.Cqrs.Abstractions;

/// <summary>Envia commands e queries para o respectivo handler, passando pelo pipeline.</summary>
public interface ISender
{
    /// <summary>Envia a requisição e retorna o resultado do handler.</summary>
    /// <exception cref="InvalidOperationException">Nenhum handler registrado para a requisição.</exception>
    /// <example>
    /// <code>
    /// Result&lt;Guid&gt; resultado = await sender.Send(new CriarClienteCommand("Maria", "12345678909"), cancellationToken);
    /// </code>
    /// </example>
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        where TResponse : Result;
}

/// <summary>Publica notificações para todos os handlers registrados.</summary>
/// <remarks>
/// Duas formas de publicar:
/// <list type="bullet">
/// <item><see cref="Publish{TNotification}"/>: imediata. Dentro de um command, roda <b>dentro da transação</b>: uma exceção
/// em um handler desfaz o command. Use para efeitos que precisam ser atômicos com o command (ex.: gravar em uma tabela de outbox).</item>
/// <item><see cref="PublishAfterCommit{TNotification}"/>: somente <b>após o commit</b>; descartada em falha ou rollback.
/// Use para efeitos externos (e-mail, integração). A entrega é de melhor esforço: para garantia de entrega, use um outbox.</item>
/// </list>
/// </remarks>
public interface IPublisher
{
    /// <summary>Publica a notificação agora (handlers executados em sequência). Sem handlers, nada acontece.</summary>
    /// <remarks>Uma exceção em um handler interrompe a publicação e é propagada para quem chamou.</remarks>
    Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification;

    /// <summary>
    /// Enfileira a notificação para ser publicada somente depois que o command confirmar a transação.
    /// Se o command falhar (Result de falha ou exceção) ou a transação for desfeita, a notificação é descartada.
    /// </summary>
    /// <remarks>
    /// <para>Deve ser chamado durante a execução de um command/query: no handler, em um behavior, authorizer ou validator.
    /// Fora do pipeline lança <see cref="InvalidOperationException"/>.</para>
    /// <para>Publicada pela requisição que confirmou a transação. Em command aninhado, sobe para o command externo e só é
    /// publicada após o commit dele (descartada se ele desfizer a transação). Sem transação (query, <c>[SkipTransaction]</c>,
    /// sem <c>IUnitOfWork</c> ou transação aberta fora do pipeline), a publicação ocorre quando a requisição mais externa
    /// termina com sucesso; exceção: um command interno que abre a própria transação (porque o externo não abriu) publica
    /// logo após o próprio commit, mesmo que o externo falhe depois.</para>
    /// <para>Os handlers rodam após a resposta do pipeline e antes do retorno do <c>Send</c>. Falhas (inclusive ao criar o
    /// handler) são registradas em log e não afetam os demais handlers nem o resultado (a operação já foi confirmada).</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// await repositorio.AdicionarAsync(cliente, cancellationToken);
    /// publisher.PublishAfterCommit(new ClienteCriadoEvent(cliente.Id));
    /// return cliente.Id;
    /// </code>
    /// </example>
    void PublishAfterCommit<TNotification>(TNotification notification)
        where TNotification : INotification;
}

/// <summary>Mediator: combina <see cref="ISender"/> e <see cref="IPublisher"/>.</summary>
/// <remarks>Prefira injetar apenas <see cref="ISender"/> ou <see cref="IPublisher"/>, conforme a necessidade.</remarks>
public interface IMediator : ISender, IPublisher
{
}
