using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Internal;

namespace TEC.Cqrs.Behaviors;

/// <summary>
/// Converte <see cref="AppException"/> (NotFoundException, BusinessException etc.) lançada no handler em
/// <see cref="Result"/> de falha com os mesmos erros. Assim o handler pode tanto retornar falha quanto lançar exceção.
/// Também converte as exceções reconhecidas pelos <see cref="IExceptionErrorMapper"/> registrados (ex.:
/// <c>FluentValidation.ValidationException</c>, com o pacote <c>TEC.Cqrs.FluentValidation</c>).
/// </summary>
/// <remarks>
/// <para>Demais exceções (bugs, banco fora do ar) são propagadas sem alteração, para o tratamento global de erros
/// (<c>UseTecExceptionHandler</c>), que responde HTTP 500 sem expor detalhes.</para>
/// <para>Não registra log: a exceção convertida fica disponível para o <c>LoggingBehavior</c>, que a anexa ao único
/// registro da falha (com a inner exception) quando o erro é interno.</para>
/// </remarks>
internal sealed class ExceptionBehavior<TRequest, TResponse>(IEnumerable<IExceptionErrorMapper> mappers, PipelineContext pipeline)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private readonly IExceptionErrorMapper[] _mappers = [.. mappers];

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }
        catch (AppException ex)
        {
            pipeline.Current?.ConvertedException = ex;
            return ResultFactory<TResponse>.Failure(ex.Errors);
        }
        catch (Exception ex) when (_mappers.TryMapException(ex, out var errors))
        {
            return ResultFactory<TResponse>.Failure(errors);
        }
    }
}
