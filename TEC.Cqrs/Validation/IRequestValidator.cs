using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;

namespace TEC.Cqrs.Validation;

/// <summary>
/// Validador de uma requisição, executado pelo pipeline depois da autorização e antes do handler. É a abstração de
/// validação do núcleo: o pacote <c>TEC.Cqrs.FluentValidation</c> a implementa para os <c>AbstractValidator&lt;T&gt;</c>.
/// </summary>
/// <remarks>
/// <para>Retorne <see cref="Result.Success()"/> quando a requisição for válida, ou falha com erros
/// <see cref="ErrorType.Validation"/> (HTTP 400), um por campo. Com vários validadores para a mesma requisição, todos são
/// executados e os erros são somados; havendo qualquer erro, o handler não é chamado.</para>
/// <para>Vale apenas para o tipo exato da requisição. Registrado pela varredura de assemblies do <c>AddTecCqrs</c> ou por
/// <c>CqrsOptions.AddRequestValidator</c> (Scoped). Um validador de tipo base, interface ou de requisição sem handler
/// registrado nunca seria executado: o <c>AddTecCqrs</c> lança <see cref="InvalidOperationException"/> na inicialização.</para>
/// <para>Com <c>CqrsOptions.RequireValidatorForCommands</c> (padrão), todo command precisa de ao menos um validador ou de
/// <see cref="SkipValidationAttribute"/>.</para>
/// </remarks>
/// <typeparam name="TRequest">Tipo exato da requisição.</typeparam>
public interface IRequestValidator<in TRequest>
    where TRequest : IBaseRequest
{
    /// <summary>Valida a requisição.</summary>
    Task<Result> ValidateAsync(TRequest request, CancellationToken cancellationToken);
}
