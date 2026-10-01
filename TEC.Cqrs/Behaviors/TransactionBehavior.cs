using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Persistence;

namespace TEC.Cqrs.Behaviors;

/// <summary>
/// Último behavior antes do handler: envolve cada command em uma transação do <see cref="IUnitOfWork"/>.
/// Commit apenas quando o <see cref="Result"/> é sucesso e nenhum command interno falhou; rollback nos demais casos.
/// </summary>
/// <remarks>
/// <para>Transação ignorada quando: a requisição é query; o command tem <see cref="SkipTransactionAttribute"/>;
/// não há <see cref="IUnitOfWork"/> registrado; ou já existe transação aberta (command dentro de command).</para>
/// <para>Command dentro de command participa da transação do externo. Se o interno falhar (<c>Result</c> de falha ou
/// exceção), a transação fica marcada para rollback: mesmo que o handler externo trate a falha e retorne sucesso, o command
/// externo desfaz a transação e retorna falha com os erros do interno (ou <see cref="NestedCommandFailed"/>, se o interno
/// lançou exceção). Vale também para o command interno com <see cref="SkipTransactionAttribute"/>; falhas de queries
/// internas não desfazem a transação.</para>
/// <para>As notificações de <see cref="IPublisher.PublishAfterCommit{TNotification}"/> são publicadas pelo mediator,
/// depois que este behavior confirma a transação.</para>
/// </remarks>
internal sealed class TransactionBehavior<TRequest, TResponse>(
    IServiceProvider serviceProvider, PipelineContext pipeline, ILogger<Mediator>? logger = null)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    /// <summary>Erro retornado pelo command externo quando um command interno lançou exceção e a transação foi desfeita.</summary>
    internal static readonly Error NestedCommandFailed =
        Error.Failure("TRANSACAO_DESFEITA", "Um command interno falhou e a transação foi desfeita.");

    private readonly ILogger _logger = logger ?? NullLogger<Mediator>.Instance;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!RequestInfo<TRequest>.IsTransactional)
            return await next(cancellationToken).ConfigureAwait(false);

        var unitOfWork = serviceProvider.GetService<IUnitOfWork>();
        if (unitOfWork is null)
            return await next(cancellationToken).ConfigureAwait(false);

        var frame = pipeline.Current;
        if (unitOfWork.HasActiveTransaction)
        {
            // Transação do pipeline ativa, mas não deste fluxo: outro Send em paralelo no mesmo escopo
            if (frame?.Parent?.FindActiveTransaction() is null && pipeline.HasPipelineTransaction)
                throw ConcurrentSend();

            // Command interno (ou transação aberta fora do pipeline): participa da transação existente
            return await next(cancellationToken).ConfigureAwait(false);
        }

        if (!pipeline.TryAcquireTransaction())
            throw ConcurrentSend();

        try
        {
            return await ExecuteInTransactionAsync(unitOfWork, frame, next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            pipeline.ReleaseTransaction();
        }
    }

    private async Task<TResponse> ExecuteInTransactionAsync(IUnitOfWork unitOfWork, RequestFrame? frame,
        RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        await unitOfWork.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var transaction = new TransactionState();
        frame?.Transaction = transaction;
        try
        {
            TResponse response;
            try
            {
                // Dentro do try: um handler que retorna null também precisa desfazer a transação (e não deixá-la aberta)
                response = await next(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"O handler de '{typeof(TRequest).FullName}' retornou null.");
            }
            catch
            {
                await RollbackSafelyAsync(unitOfWork, transaction).ConfigureAwait(false);
                throw;
            }

            if (response.IsFailure)
            {
                await RollbackSafelyAsync(unitOfWork, transaction).ConfigureAwait(false);
                return response;
            }

            if (transaction.IsRollbackOnly)
            {
                // O handler externo tratou a falha do interno, mas o que o interno gravou não pode ser confirmado pela metade
                await RollbackSafelyAsync(unitOfWork, transaction).ConfigureAwait(false);
                CqrsLog.NestedCommandRollback(_logger, RequestInfo<TRequest>.Name);
                return ResultFactory<TResponse>.Failure(transaction.RollbackErrors ?? [NestedCommandFailed]);
            }

            try
            {
                // Commit não é cancelado: interromper no meio deixaria o resultado ambíguo (gravou ou não?) se o cliente desconectar
                await unitOfWork.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                await RollbackSafelyAsync(unitOfWork, transaction).ConfigureAwait(false);
                throw;
            }

            transaction.Committed = true;
            return response;
        }
        finally
        {
            transaction.IsActive = false;
        }
    }

    private static InvalidOperationException ConcurrentSend() => new(
        $"O command '{typeof(TRequest).FullName}' foi enviado em paralelo a outro command com transação no mesmo escopo. " +
        "O IUnitOfWork (e o DbContext) do escopo não suporta operações simultâneas: aguarde cada Send antes do próximo " +
        "ou crie um escopo por operação (IServiceScopeFactory).");

    /// <summary>Rollback: uma falha aqui é registrada, mas não substitui a exceção ou o resultado de falha original.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A exceção ou o resultado de falha original (mais relevante) é mantido pelo chamador.")]
    private async Task RollbackSafelyAsync(IUnitOfWork unitOfWork, TransactionState transaction)
    {
        transaction.IsActive = false;
        try
        {
            // Rollback não é cancelado: a transação precisa ser encerrada mesmo que a requisição tenha sido abortada
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CqrsLog.RollbackFailed(_logger, ex, RequestInfo<TRequest>.Name);
        }
    }
}
