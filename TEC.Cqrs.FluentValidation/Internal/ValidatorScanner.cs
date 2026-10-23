namespace TEC.Cqrs.FluentValidation.Internal;

/// <summary>
/// Mensagens de trimming/AOT da varredura de assemblies em busca de validators do FluentValidation. A varredura em si é
/// a do núcleo (<c>TEC.Cqrs.Internal.TypeScanner</c>): tipos concretos em ordem de nome completo (ordem determinística de
/// execução dos validators) e erro claro quando algum tipo do assembly não carrega.
/// </summary>
internal static class ValidatorScanner
{
    public const string ScanningMessage =
        "A varredura de assemblies usa reflexão e pode não encontrar validators removidos pelo trimming. Em apps com " +
        "trimming ou Native AOT, registre os validators com AddFluentValidation(fv => fv.AddValidator<TRequest, TValidator>()).";

    public const string DynamicCodeMessage =
        "O registro por varredura cria tipos genéricos em tempo de execução (MakeGenericType). Em Native AOT, use AddValidator.";
}
