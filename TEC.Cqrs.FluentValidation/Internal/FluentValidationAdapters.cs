using System.Diagnostics.CodeAnalysis;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Validation;
using FluentValidationException = global::FluentValidation.ValidationException;
using IValidator = global::FluentValidation.IValidator;

namespace TEC.Cqrs.FluentValidation.Internal;

/// <summary>
/// Liga os <c>IValidator&lt;TRequest&gt;</c> do FluentValidation ao pipeline (<see cref="IRequestValidator{TRequest}"/>).
/// Registrado uma vez por tipo de requisição que tem validator.
/// </summary>
internal sealed class FluentValidationRequestValidator<TRequest>(
    IEnumerable<global::FluentValidation.IValidator<TRequest>> validators, CqrsFluentValidationOptions options)
    : IRequestValidator<TRequest>
    where TRequest : IBaseRequest
{
    private readonly global::FluentValidation.IValidator<TRequest>[] _validators = [.. validators];

    public async Task<Result> ValidateAsync(TRequest request, CancellationToken cancellationToken)
    {
        List<Error> errors = [];

        foreach (var validator in _validators)
        {
            // Um contexto por validator: o ValidationContext acumula as falhas, e o resultado de cada validator
            // traria também as dos anteriores (erros duplicados)
            var context = new global::FluentValidation.ValidationContext<TRequest>(request);
            var result = await ((IValidator)validator).ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            errors.AddRange(ValidationErrorMapper.ToErrors(result.Errors, options.CamelCaseValidationFields));
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure([.. errors]);
    }
}

/// <summary>
/// Converte a <c>FluentValidation.ValidationException</c> (ex.: <c>ValidateAndThrow</c> no handler) em erros de validação,
/// no pipeline e no <c>UseTecExceptionHandler</c>.
/// </summary>
internal sealed class FluentValidationExceptionMapper(CqrsFluentValidationOptions options) : IExceptionErrorMapper
{
    public bool TryMap(Exception exception, [NotNullWhen(true)] out IReadOnlyList<Error>? errors)
    {
        if (exception is FluentValidationException validation)
        {
            errors = ValidationErrorMapper.ToErrorsOrDefault(validation.Errors, options.CamelCaseValidationFields);
            return true;
        }

        errors = null;
        return false;
    }
}
