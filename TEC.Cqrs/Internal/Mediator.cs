using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Implementação do mediator. Resolve handler e behaviors no escopo atual e monta o pipeline.
/// Os executores genéricos de cada requisição e os alvos de cada notificação vêm do <see cref="CqrsRegistry"/>
/// (montados no registro, sem reflexão em execução).
/// </summary>
internal sealed class Mediator(IServiceProvider serviceProvider, PipelineContext pipeline, CqrsRegistry registry) : IMediator
{
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        where TResponse : Result
    {
        ArgumentNullException.ThrowIfNull(request);
        return registry.GetHandler<TResponse>(request.GetType()).Handle(request, serviceProvider, pipeline, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Usa o tipo real (e não o estático) para encontrar os handlers corretos
        var targets = registry.GetNotificationTargets(notification.GetType(), NotificationTarget<TNotification>.Instance);
        return NotificationPublisher.PublishAsync(targets, notification, serviceProvider, cancellationToken);
    }

    public void PublishAfterCommit<TNotification>(TNotification notification)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        pipeline.Enqueue(new DeferredNotification(notification, NotificationTarget<TNotification>.Instance));
    }
}

/// <summary>Notificação pós-commit pendente, com o alvo do tipo estático usado no <c>PublishAfterCommit</c>.</summary>
internal readonly record struct DeferredNotification(INotification Notification, NotificationTarget StaticTarget);

/// <summary>
/// Executa os handlers de uma notificação e também os registrados para as classes base e interfaces dela
/// (ex.: <c>INotificationHandler&lt;IEventoDeCliente&gt;</c>). Cada classe de handler roda uma única vez por
/// publicação, pelo tipo mais específico que ela trata.
/// </summary>
internal static class NotificationPublisher
{
    public static async Task PublishAsync(NotificationTarget[] targets, INotification notification, IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        using var telemetry = NotificationTelemetry.Start(notification, serviceProvider, afterCommit: false);
        HashSet<Type> executed = [];

        try
        {
            foreach (var target in targets)
            {
                foreach (var handler in target.Resolve(serviceProvider))
                {
                    if (handler is not null && executed.Add(handler.GetType()))
                        await target.Invoke(handler, notification, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            telemetry.HandlerFailed(ex);
            telemetry.Complete();
            throw;
        }

        telemetry.Complete();
    }

    /// <summary>Publica uma notificação pós-commit: cada handler é isolado (falha registrada em log, sem interromper os demais).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Após o commit a operação já foi confirmada: a falha de um handler é registrada e não afeta os demais nem o chamador.")]
    public static async Task PublishIsolatedAsync(CqrsRegistry registry, DeferredNotification deferred, IServiceProvider serviceProvider,
        ILogger logger)
    {
        var notification = deferred.Notification;
        string notificationName = notification.GetType().Name;
        using var telemetry = NotificationTelemetry.Start(notification, serviceProvider, afterCommit: true);
        HashSet<Type> executed = [];

        foreach (var target in registry.GetNotificationTargets(notification.GetType(), deferred.StaticTarget))
        {
            object?[] handlers;
            try
            {
                // Dentro do try: um handler que não pode ser criado (dependência ausente, erro no construtor) não pode
                // chegar ao chamador depois do commit
                handlers = [.. target.Resolve(serviceProvider)];
            }
            catch (Exception ex)
            {
                telemetry.HandlerFailed(ex);
                CqrsLog.DeferredNotificationResolutionFailed(logger, ex, notificationName, $"INotificationHandler<{target.TargetType.Name}>");
                continue;
            }

            foreach (var handler in handlers)
            {
                if (handler is null || !executed.Add(handler.GetType()))
                    continue;

                try
                {
                    // Não é cancelado: a operação já foi confirmada e o efeito colateral deve acontecer mesmo se o cliente desconectar
                    await target.Invoke(handler, notification, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    telemetry.HandlerFailed(ex);
                    CqrsLog.DeferredNotificationFailed(logger, ex, notificationName, handler.GetType().Name);
                }
            }
        }

        telemetry.Complete();
    }
}

internal abstract class RequestHandlerWrapper
{
}

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapper
    where TResponse : Result
{
    public abstract Task<TResponse> Handle(object request, IServiceProvider serviceProvider, PipelineContext pipeline,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executa uma requisição: empilha a requisição no <see cref="PipelineContext"/> (fora de todos os behaviors), monta o
/// pipeline e, ao final, decide o destino das notificações pós-commit e da transação externa.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Sucesso: publica as notificações pendentes se esta requisição confirmou a própria transação ou é a mais externa;
/// senão, repassa-as para a requisição externa (publicadas após o commit dela). A mais externa dentro de uma transação
/// aberta fora do pipeline descarta-as e lança <see cref="InvalidOperationException"/>.</item>
/// <item>Falha ou exceção: descarta as notificações pendentes e, se for um command (com ou sem <c>[SkipTransaction]</c>)
/// dentro da transação de outro command, marca essa transação para rollback (o command externo desfaz tudo e retorna falha).</item>
/// </list>
/// </remarks>
internal sealed class RequestHandlerWrapperImpl<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    /// <summary>
    /// Máximo de rodadas de publicação pós-commit por requisição: a primeira publica as notificações do <c>Send</c>; as
    /// seguintes, as enfileiradas pelos próprios handlers pós-commit.
    /// </summary>
    internal const int MaxDeferredRounds = 10;

    public override async Task<TResponse> Handle(object request, IServiceProvider serviceProvider, PipelineContext pipeline,
        CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var frame = pipeline.Enter(RequestInfo<TRequest>.IsCommand);
        try
        {
            TResponse response;
            try
            {
                response = await BuildPipeline(typedRequest, serviceProvider)(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"O pipeline de '{typeof(TRequest).FullName}' retornou null: um behavior retornou null em vez de um Result.");
            }
            catch
            {
                MarkEnclosingTransactionForRollback(frame, errors: null);
                throw;
            }

            if (response.IsFailure)
            {
                MarkEnclosingTransactionForRollback(frame, response.Errors);
                return response;
            }

            if (frame.Transaction is { Committed: true })
                await PublishDeferredAsync(frame, serviceProvider).ConfigureAwait(false);
            else if (frame.Parent is null)
            {
                EnsureNoExternalTransaction(frame, serviceProvider);
                await PublishDeferredAsync(frame, serviceProvider).ConfigureAwait(false);
            }
            else
            {
                foreach (var notification in frame.TakeAll())
                    frame.Parent.Enqueue(notification);
            }

            return response;
        }
        finally
        {
            pipeline.Exit(frame);
        }
    }

    private static RequestHandlerDelegate<TResponse> BuildPipeline(TRequest request, IServiceProvider serviceProvider)
    {
        var handler = serviceProvider.GetService<IRequestHandler<TRequest, TResponse>>()
            ?? throw new InvalidOperationException(
                $"Nenhum handler registrado para '{typeof(TRequest).FullName}'. " +
                "Verifique se o handler foi registrado no AddTecCqrs (varredura do assembly ou AddCommandHandler/AddQueryHandler).");

        RequestHandlerDelegate<TResponse> pipeline = token => InvokeHandler(handler, request, token);

        // O primeiro behavior registrado é o mais externo: monta a cadeia de trás para frente
        // O container da Microsoft já devolve um array: evita a cópia por requisição (caminho quente)
        var services = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>();
        var behaviors = services as IPipelineBehavior<TRequest, TResponse>[] ?? [.. services];
        for (int i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var next = pipeline;
            pipeline = token => behavior.Handle(request, next, token);
        }

        return pipeline;
    }

    /// <summary>
    /// Chama o handler e garante que ele retornou um <see cref="Result"/> (e não <c>null</c>), para queries e commands:
    /// um <c>null</c> viraria <see cref="NullReferenceException"/> nos behaviors, sem indicar o handler culpado.
    /// </summary>
    private static Task<TResponse> InvokeHandler(IRequestHandler<TRequest, TResponse> handler, TRequest request,
        CancellationToken cancellationToken)
    {
        var task = handler.Handle(request, cancellationToken) ?? throw HandlerReturnedNull(handler);

        // Caminho síncrono (handler já concluído) sem máquina de estado
        return task.IsCompletedSuccessfully && task.Result is not null ? task : AwaitHandlerAsync(task, handler);
    }

    private static async Task<TResponse> AwaitHandlerAsync(Task<TResponse> task, IRequestHandler<TRequest, TResponse> handler) =>
        await task.ConfigureAwait(false) ?? throw HandlerReturnedNull(handler);

    private static InvalidOperationException HandlerReturnedNull(IRequestHandler<TRequest, TResponse> handler) => new(
        $"O handler de '{typeof(TRequest).FullName}' ({handler.GetType().FullName}) retornou null. " +
        "Retorne sempre um Result (Result.Success(), Result.Failure(...) ou o valor).");

    /// <summary>
    /// Command interno que falhou: a transação do command externo não pode mais ser confirmada. Vale também para o command
    /// interno com <c>[SkipTransaction]</c> (ele só deixa de abrir a própria transação); queries internas não marcam.
    /// </summary>
    private static void MarkEnclosingTransactionForRollback(RequestFrame frame, IReadOnlyList<Error>? errors)
    {
        if (frame.IsCommand && frame.Parent?.FindActiveTransaction() is { } transaction)
            transaction.MarkRollbackOnly(errors);
    }

    /// <summary>
    /// Requisição mais externa que não confirmou a própria transação, com notificações pós-commit pendentes e uma
    /// transação aberta fora do pipeline (<c>BeginTransactionAsync</c> antes do <c>Send</c>): o pipeline não sabe quando
    /// ela será confirmada, e publicar agora anunciaria dados que ainda podem ser desfeitos. Descarta as notificações e
    /// lança <see cref="InvalidOperationException"/> (quem abriu a transação deve desfazê-la).
    /// </summary>
    private static void EnsureNoExternalTransaction(RequestFrame frame, IServiceProvider serviceProvider)
    {
        if (!frame.HasPending || serviceProvider.GetService<Persistence.IUnitOfWork>() is not { HasActiveTransaction: true })
            return;

        var discarded = frame.TakeAll();
        throw new InvalidOperationException(
            $"A requisição '{typeof(TRequest).FullName}' chamou PublishAfterCommit ({discarded.Length} notificação(ões), " +
            $"a primeira '{discarded[0].Notification.GetType().Name}'), mas roda dentro de uma transação aberta fora do " +
            "pipeline (IUnitOfWork.BeginTransactionAsync antes do Send). O pipeline não sabe quando essa transação será " +
            "confirmada, então as notificações foram descartadas para não anunciar dados que ainda podem ser desfeitos. " +
            "Deixe o pipeline controlar a transação (um command externo que envia os demais) ou, se precisar abri-la fora, " +
            "use Publish depois do seu commit.");
    }

    /// <summary>
    /// Publica as notificações pendentes. Repete enquanto os handlers enfileirarem novas notificações
    /// (a requisição continua no topo da pilha durante a publicação), até <see cref="MaxDeferredRounds"/> rodadas.
    /// </summary>
    /// <remarks>
    /// Um handler pós-commit que chama <c>PublishAfterCommit</c> gera uma nova rodada; um ciclo (A publica B, B publica A)
    /// laçaria para sempre. Ao exceder o limite, as notificações restantes são descartadas com log de erro (evento 1016):
    /// a operação já foi confirmada e o chamador não recebe exceção.
    /// </remarks>
    private static async Task PublishDeferredAsync(RequestFrame frame, IServiceProvider serviceProvider)
    {
        var pending = frame.TakeAll();
        if (pending.Length == 0)
            return;

        var registry = serviceProvider.GetRequiredService<CqrsRegistry>();
        var logger = serviceProvider.GetService<ILogger<Mediator>>() ?? (ILogger)NullLogger<Mediator>.Instance;
        for (int round = 1; pending.Length > 0; round++, pending = frame.TakeAll())
        {
            if (round > MaxDeferredRounds)
            {
                CqrsLog.DeferredNotificationRoundsExceeded(logger, RequestInfo<TRequest>.Name, MaxDeferredRounds, pending.Length,
                    pending[0].Notification.GetType().Name);
                return;
            }

            foreach (var notification in pending)
                await NotificationPublisher.PublishIsolatedAsync(registry, notification, serviceProvider, logger).ConfigureAwait(false);
        }
    }
}
