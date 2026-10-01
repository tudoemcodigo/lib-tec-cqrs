using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Behaviors;

/// <summary>
/// Executa os validadores (<see cref="IRequestValidator{TRequest}"/>) da requisição antes do handler. Havendo erros, o
/// handler não é chamado e o pipeline retorna falha (tipicamente <see cref="ErrorType.Validation"/>, HTTP 400) com todos
/// os erros de todos os validadores.
/// </summary>
/// <remarks>
/// Com <see cref="CqrsOptions.RequireValidatorForCommands"/> (padrão), command sem validador e sem
/// <see cref="SkipValidationAttribute"/> lança <see cref="InvalidOperationException"/> (fail closed), inclusive quando
/// nenhum pacote de validação foi registrado.
/// </remarks>
internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IRequestValidator<TRequest>> validators, CqrsOptions options)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private readonly IRequestValidator<TRequest>[] _validators = [.. validators];

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_validators.Length == 0)
        {
            if (options.RequireValidatorForCommands && RequestInfo<TRequest>.RequiresValidator)
            {
                throw new InvalidOperationException(
                    $"O command '{typeof(TRequest).FullName}' não possui validator. Crie um AbstractValidator<{typeof(TRequest).Name}> " +
                    "(pacote TEC.Cqrs.FluentValidation, com .AddFluentValidation()) ou um IRequestValidator ou, se o command não " +
                    "recebe dados do usuário, marque-o com [SkipValidation].");
            }

            return await next(cancellationToken).ConfigureAwait(false);
        }

        List<Error> errors = [];
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"O validador '{validator.GetType().FullName}' retornou null.");

            if (result.IsFailure)
                errors.AddRange(result.Errors);
        }

        return errors.Count == 0
            ? await next(cancellationToken).ConfigureAwait(false)
            : ResultFactory<TResponse>.Failure(errors);
    }
}
