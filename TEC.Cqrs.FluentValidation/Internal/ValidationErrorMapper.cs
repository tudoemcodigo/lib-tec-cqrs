using System.Text.Json;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using ValidationFailure = global::FluentValidation.Results.ValidationFailure;

namespace TEC.Cqrs.FluentValidation.Internal;

/// <summary>Converte falhas do FluentValidation em <see cref="Error"/> de validação (um por campo).</summary>
/// <remarks>
/// A mensagem da falha é repassada ao cliente sem alteração (o valor tentado, <c>AttemptedValue</c>, nunca é copiado para
/// o erro). Templates com <c>{PropertyValue}</c> ou <c>{ComparisonValue}</c> ecoam o valor na mensagem: em campos
/// sensíveis (senha, token), a regra deve usar <c>WithMessage</c> com texto fixo, sem placeholders de valor.
/// </remarks>
internal static class ValidationErrorMapper
{
    private const string DefaultMessage = "Valor inválido.";

    /// <summary>Converte as falhas; se não houver nenhuma, retorna um erro genérico de validação.</summary>
    public static Error[] ToErrorsOrDefault(IEnumerable<ValidationFailure>? failures, bool camelCaseFields)
    {
        Error[] errors = [.. ToErrors(failures, camelCaseFields)];
        return errors.Length > 0
            ? errors
            : [Error.Validation(RequestValidationException.DefaultCode, RequestValidationException.DefaultMessage)];
    }

    public static IEnumerable<Error> ToErrors(IEnumerable<ValidationFailure>? failures, bool camelCaseFields) =>
        (failures ?? []).Where(f => f is not null).Select(f => ToError(f, camelCaseFields));

    private static Error ToError(ValidationFailure failure, bool camelCaseFields)
    {
        // O código padrão do FluentValidation (ex.: "NotEmptyValidator") é mantido; personalize com .WithErrorCode("NOME_OBRIGATORIO")
        string code = string.IsNullOrWhiteSpace(failure.ErrorCode) ? RequestValidationException.DefaultCode : failure.ErrorCode;
        string message = string.IsNullOrWhiteSpace(failure.ErrorMessage) ? DefaultMessage : failure.ErrorMessage;

        return Error.Validation(code, message, FormatField(failure.PropertyName, camelCaseFields));
    }

    private static string? FormatField(string? propertyName, bool camelCase)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return null;
        if (!camelCase)
            return propertyName;

        // "Endereco.Cep" → "endereco.cep"; "Itens[0].Quantidade" → "itens[0].quantidade" (mesmo padrão do JSON da API)
        return string.Join('.', propertyName.Split('.').Select(JsonNamingPolicy.CamelCase.ConvertName));
    }
}
