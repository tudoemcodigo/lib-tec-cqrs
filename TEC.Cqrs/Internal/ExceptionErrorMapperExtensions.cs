using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Conversão de exceções pelos <see cref="IExceptionErrorMapper"/> registrados, compartilhada pelo behavior de exceções do
/// pipeline e pelo <c>UseTecExceptionHandler</c> do <c>TEC.Cqrs.AspNetCore</c>.
/// </summary>
internal static class ExceptionErrorMapperExtensions
{
    /// <summary>Converte a exceção pelo primeiro mapper que a reconhecer (com ao menos um erro).</summary>
    public static bool TryMapException(this IEnumerable<IExceptionErrorMapper> mappers, Exception exception,
        out IReadOnlyList<Error> errors)
    {
        foreach (var mapper in mappers)
        {
            if (mapper.TryMap(exception, out var mapped) && mapped.Count > 0)
            {
                errors = mapped;
                return true;
            }
        }

        errors = [];
        return false;
    }
}
