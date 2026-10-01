namespace TEC.Cqrs.Validation;

/// <summary>
/// Dispensa o command de ter validator (quando <c>CqrsOptions.RequireValidatorForCommands</c> está ativo, o padrão).
/// Use apenas em commands sem dados de entrada ou cujos dados não vêm do usuário.
/// </summary>
/// <example>
/// <code>
/// [SkipValidation]
/// public sealed record ProcessarFilaCommand : ICommand;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class SkipValidationAttribute : Attribute
{
}
