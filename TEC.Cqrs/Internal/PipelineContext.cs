#if NET9_0_OR_GREATER
using SyncLock = System.Threading.Lock;
#else
using SyncLock = object;
#endif
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Estado do pipeline em um escopo: a pilha de requisições em execução (uma por fluxo assíncrono) e o controle das
/// transações abertas pelo pipeline.
/// </summary>
/// <remarks>
/// <para>A requisição atual é guardada em um <see cref="AsyncLocal{T}"/> da instância (Scoped): cada fluxo assíncrono tem a
/// sua própria pilha. Assim, <c>Send</c> concorrentes no mesmo escopo (ex.: <c>Task.WhenAll</c>) não misturam profundidade
/// nem notificações, e um novo escopo criado dentro de um handler começa com a pilha vazia.</para>
/// <para>A pilha é controlada pelo <c>RequestHandlerWrapper</c> do mediator (fora de todos os behaviors), então
/// <see cref="IPublisher.PublishAfterCommit{TNotification}"/> funciona em qualquer ponto do <c>Send</c>: behaviors,
/// authorizers, validators e handler.</para>
/// </remarks>
internal sealed class PipelineContext
{
    private readonly AsyncLocal<RequestFrame?> _current = new();

    // 1 enquanto uma transação aberta pelo pipeline estiver ativa neste escopo
    private int _pipelineTransaction;

    /// <summary>Requisição em execução neste fluxo, ou <c>null</c> fora do pipeline.</summary>
    public RequestFrame? Current => _current.Value;

    /// <summary>Empilha a requisição neste fluxo. Desempilhe com <see cref="Exit"/>.</summary>
    public RequestFrame Enter(bool isCommand)
    {
        var frame = new RequestFrame(_current.Value, isCommand);
        _current.Value = frame;
        return frame;
    }

    public void Exit(RequestFrame frame) => _current.Value = frame.Parent;

    public void Enqueue(DeferredNotification notification)
    {
        var frame = _current.Value ?? throw new InvalidOperationException(
            "PublishAfterCommit só pode ser chamado durante a execução de um command/query (handler, behavior, " +
            "authorizer ou validator). Fora do pipeline, use Publish.");

        frame.Enqueue(notification);
    }

    /// <summary>Reserva a única transação do pipeline no escopo; <c>false</c> se outra já estiver ativa (Send concorrente).</summary>
    public bool TryAcquireTransaction() => Interlocked.CompareExchange(ref _pipelineTransaction, 1, 0) == 0;

    public void ReleaseTransaction() => Volatile.Write(ref _pipelineTransaction, 0);

    /// <summary>Há uma transação aberta pelo pipeline neste escopo (em qualquer fluxo).</summary>
    public bool HasPipelineTransaction => Volatile.Read(ref _pipelineTransaction) == 1;
}

/// <summary>Uma requisição em execução: notificações pós-commit pendentes e, se ela abriu, a transação.</summary>
internal sealed class RequestFrame(RequestFrame? parent, bool isCommand)
{
    private readonly SyncLock _lock = new();
    private List<DeferredNotification>? _deferred;

    public RequestFrame? Parent { get; } = parent;

    /// <summary>
    /// A requisição é um command (com ou sem <c>[SkipTransaction]</c>): se falhar dentro da transação de outro command,
    /// essa transação é marcada para rollback.
    /// </summary>
    public bool IsCommand { get; } = isCommand;

    /// <summary>Transação aberta por esta requisição (definida pelo <c>TransactionBehavior</c>).</summary>
    public TransactionState? Transaction { get; set; }

    /// <summary>
    /// Exceção convertida em <c>Result</c> pelo <c>ExceptionBehavior</c>, para o <c>LoggingBehavior</c> registrá-la uma única vez.
    /// </summary>
    public Exception? ConvertedException { get; set; }

    public void Enqueue(DeferredNotification notification)
    {
        lock (_lock)
            (_deferred ??= []).Add(notification);
    }

    /// <summary>Remove e retorna as notificações pendentes.</summary>
    public DeferredNotification[] TakeAll()
    {
        lock (_lock)
        {
            if (_deferred is not { Count: > 0 })
                return [];

            DeferredNotification[] taken = [.. _deferred];
            _deferred.Clear();
            return taken;
        }
    }

    /// <summary>Transação ativa mais próxima (desta requisição ou de uma externa), ou <c>null</c>.</summary>
    public TransactionState? FindActiveTransaction()
    {
        for (var frame = this; frame is not null; frame = frame.Parent)
        {
            if (frame.Transaction is { IsActive: true } transaction)
                return transaction;
        }

        return null;
    }
}

/// <summary>Transação aberta pelo pipeline. Um command interno que falha a marca para rollback (rollback-only).</summary>
internal sealed class TransactionState
{
    private readonly SyncLock _lock = new();
    private bool _rollbackOnly;
    private Error[]? _rollbackErrors;

    public bool IsActive { get; set; } = true;

    public bool Committed { get; set; }

    public bool IsRollbackOnly
    {
        get
        {
            lock (_lock)
                return _rollbackOnly;
        }
    }

    /// <summary>Erros do primeiro command interno que falhou (<c>null</c> se ele lançou exceção).</summary>
    public Error[]? RollbackErrors
    {
        get
        {
            lock (_lock)
                return _rollbackErrors;
        }
    }

    /// <summary>Marca a transação para rollback. Mantém os erros da primeira falha.</summary>
    public void MarkRollbackOnly(IReadOnlyList<Error>? errors)
    {
        lock (_lock)
        {
            if (_rollbackOnly)
                return;

            _rollbackOnly = true;
            _rollbackErrors = errors is { Count: > 0 } ? [.. errors] : null;
        }
    }
}
