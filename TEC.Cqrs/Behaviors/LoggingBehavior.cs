using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.Internal;

namespace TEC.Cqrs.Behaviors;

/// <summary>
/// Primeiro behavior do pipeline: registra log, <see cref="Activity"/> e métricas (OpenTelemetry) de cada requisição,
/// com duração, resultado e códigos de erro. O conteúdo da requisição nunca é registrado.
/// </summary>
/// <remarks>
/// <para>Cada falha gera um único registro: a exceção convertida pelo <c>ExceptionBehavior</c> (<c>AppException</c> ou
/// <c>IExceptionErrorMapper</c>) é anexada
/// ao log da falha, e uma exceção não tratada é registrada uma vez só (nem as requisições externas nem o
/// <c>UseTecExceptionHandler</c> a registram de novo).</para>
/// <para>Em exceção, o evento <c>exception</c> da <see cref="Activity"/> leva só o tipo da exceção; mensagem e stack trace
/// apenas com <see cref="CqrsOptions.RecordExceptionDetailsInTraces"/> (o log sempre recebe a exceção completa).</para>
/// <para>Cancelamento solicitado pelo token da requisição é registrado em nível Debug, com <c>cqrs.canceled = true</c> na
/// <see cref="Activity"/>, sem marcá-la como erro.</para>
/// </remarks>
internal sealed class LoggingBehavior<TRequest, TResponse>(
    PipelineContext pipeline, CqrsMetrics metrics, CqrsOptions options, ILogger<Mediator>? logger = null)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private const string Success = "success";
    private const string Failure = "failure";
    private const string ExceptionOutcome = "exception";
    private const string Canceled = "canceled";

    private static readonly string FullName = typeof(TRequest).FullName ?? typeof(TRequest).Name;

    private readonly ILogger _logger = logger ?? NullLogger<Mediator>.Instance;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        string name = RequestInfo<TRequest>.Name;
        string kind = RequestInfo<TRequest>.Kind;

        using var activity = CqrsDiagnostics.ActivitySource.StartActivity(name);
        activity?.SetTag(CqrsDiagnostics.RequestTag, FullName);
        activity?.SetTag(CqrsDiagnostics.KindTag, kind);

        CqrsLog.Handling(_logger, kind, name);
        long start = Stopwatch.GetTimestamp();

        try
        {
            // Handler que retorna null já é barrado pelo mediator; aqui só sobra um behavior próprio que retornou null
            var response = await next(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"O pipeline de '{FullName}' retornou null: um behavior retornou null em vez de um Result.");
            var elapsed = Stopwatch.GetElapsedTime(start);

            activity?.SetTag("cqrs.success", response.IsSuccess);
            if (response.IsSuccess)
            {
                CqrsLog.Handled(_logger, kind, name, (long)elapsed.TotalMilliseconds);
                Record(activity, kind, Success, errorType: null, elapsed);
                return response;
            }

            string codes = string.Join(", ", response.Errors.Select(e => e.Code));

            // Falhas internas/integração são erros do sistema; as demais (validação, 404, regra de negócio) são fluxo esperado.
            // Basta um erro interno (em qualquer posição) para a falha ser interna, como no ApiResponse do TEC.Core.
            var internalError = response.Errors.FirstOrDefault(e => !e.Type.IsExposedToClient());
            var reported = internalError ?? response.Errors[0];
            activity?.SetTag("cqrs.error_code", reported.Code);
            if (internalError is null)
            {
                CqrsLog.HandledWithFailure(_logger, kind, name, reported.Type.ToString(), codes, (long)elapsed.TotalMilliseconds);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Error, internalError.Code);
                CqrsLog.HandledWithInternalFailure(_logger, pipeline.Current?.ConvertedException, kind, name,
                    internalError.Type.ToString(), codes, (long)elapsed.TotalMilliseconds);
            }

            Record(activity, kind, Failure, reported.Type.ToString(), elapsed);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelamento pedido por quem chamou (ex.: cliente desconectou): não é erro do sistema
            var elapsed = Stopwatch.GetElapsedTime(start);
            activity?.SetTag("cqrs.success", false);
            activity?.SetTag("cqrs.canceled", true);
            CqrsLog.Canceled(_logger, kind, name, (long)elapsed.TotalMilliseconds);
            Record(activity, kind, Canceled, errorType: null, elapsed);
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            activity?.SetTag("cqrs.success", false);
            activity?.RecordException(ex, options.RecordExceptionDetailsInTraces);
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            Record(activity, kind, ExceptionOutcome, ex.GetType().FullName ?? ex.GetType().Name, elapsed);

            // Registrada uma única vez: na requisição mais interna em que ocorreu. Só é marcada se o log for de fato
            // escrito: com o nível Error desabilitado, quem trata a exceção depois (UseTecExceptionHandler) ainda a registra
            if (_logger.IsEnabled(LogLevel.Error) && LoggedExceptions.TryMark(ex))
                CqrsLog.UnhandledException(_logger, ex, kind, name, (long)elapsed.TotalMilliseconds);
            throw;
        }
    }

    /// <summary>Resultado na <see cref="Activity"/> (mesmos atributos das métricas) e nas métricas.</summary>
    private void Record(Activity? activity, string kind, string outcome, string? errorType, TimeSpan elapsed)
    {
        if (activity is not null)
        {
            activity.SetTag(CqrsDiagnostics.OutcomeTag, outcome);
            if (errorType is not null)
                activity.SetTag(CqrsDiagnostics.ErrorTypeTag, errorType);
        }

        if (metrics.Enabled)
            metrics.Record(FullName, kind, outcome, errorType, elapsed);
    }
}
