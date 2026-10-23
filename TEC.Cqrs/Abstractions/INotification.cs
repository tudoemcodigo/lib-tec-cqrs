namespace TEC.Cqrs.Abstractions;

/// <summary>
/// Notificação (evento) publicada pelo <see cref="IPublisher"/>. Pode ter zero ou vários handlers.
/// </summary>
/// <example>
/// <code>
/// public sealed record ClienteCriadoEvent(Guid ClienteId) : INotification;
/// </code>
/// </example>
public interface INotification
{
}

/// <summary>Handler de uma notificação.</summary>
/// <remarks>
/// <para>Os handlers são executados em sequência, em ordem determinística: primeiro os do tipo exato da notificação,
/// depois os das classes base (da mais próxima para a mais distante) e por fim os das interfaces (por nome completo).
/// Dentro de cada tipo, vale a ordem de registro: assemblies na ordem informada ao <c>AddTecCqrs</c> e, dentro de cada
/// assembly, os handlers em ordem alfabética do nome completo (namespace + classe).</para>
/// <para>Um handler de tipo base ou interface (ex.: <c>INotificationHandler&lt;IEventoDeCliente&gt;</c>) recebe as
/// notificações concretas que o implementam. Cada classe de handler roda uma única vez por publicação, pelo tipo mais
/// específico que ela trata.</para>
/// <para>Com <see cref="IPublisher.Publish{TNotification}"/>, uma exceção em um handler interrompe a publicação e é
/// propagada para quem chamou; com <see cref="IPublisher.PublishAfterCommit{TNotification}"/>, é registrada em log e os
/// demais handlers continuam.</para>
/// </remarks>
public interface INotificationHandler<in TNotification>
    where TNotification : INotification
{
    /// <summary>Processa a notificação.</summary>
    Task Handle(TNotification notification, CancellationToken cancellationToken);
}
