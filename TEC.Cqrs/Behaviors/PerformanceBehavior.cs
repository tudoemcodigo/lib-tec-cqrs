using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Internal;

namespace TEC.Cqrs.Behaviors;

/// <summary>
/// Registra aviso quando a execução passa de <see cref="CqrsOptions.SlowRequestThreshold"/>.
/// </summary>
/// <remarks>
/// Mede o que vem depois dele no pipeline: abertura da transação, handler e commit/rollback. Não inclui autorização,
/// validação, behaviors próprios nem as notificações pós-commit. Também mede execuções que terminam com exceção
/// (ex.: timeout no banco), registradas como lentas se passarem do limite.
/// </remarks>
internal sealed class PerformanceBehavior<TRequest, TResponse>(CqrsOptions options, ILogger<Mediator>? logger = null)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private readonly ILogger _logger = logger ?? NullLogger<Mediator>.Instance;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (options.SlowRequestThreshold is not { } threshold)
            return await next(cancellationToken).ConfigureAwait(false);

        long start = Stopwatch.GetTimestamp();
        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed > threshold)
            {
                CqrsLog.SlowRequest(_logger, RequestInfo<TRequest>.Kind, RequestInfo<TRequest>.Name,
                    (long)elapsed.TotalMilliseconds, (long)threshold.TotalMilliseconds);
            }
        }
    }
}
