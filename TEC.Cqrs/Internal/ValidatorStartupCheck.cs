using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Verificação de <see cref="CqrsOptions.RequireValidatorForCommands"/> na subida da aplicação: os validadores só ficam
/// completos depois do <c>AddTecCqrs</c> (ex.: <c>.AddFluentValidation()</c> ou registros diretos no container), então ela
/// guarda o <see cref="IServiceCollection"/> e roda quando o host inicia (o <c>TEC.Cqrs.AspNetCore</c> a executa por um
/// <c>IHostedService</c>). A verificação em execução, no <c>ValidationBehavior</c>, continua valendo.
/// </summary>
internal sealed class ValidatorStartupCheck(IServiceCollection services, CqrsOptions options)
{
    private IServiceCollection? _services = services;

    /// <summary>
    /// Lança <see cref="InvalidOperationException"/> listando os commands conhecidos (registrados no <c>AddTecCqrs</c> ou
    /// encontrados na varredura) sem validador e sem <see cref="SkipValidationAttribute"/>. Roda uma única vez.
    /// </summary>
    public void Run()
    {
        // Solta a referência ao container: a verificação só faz sentido na subida
        var registered = Interlocked.Exchange(ref _services, null);
        if (registered is null || !options.RequireValidatorForCommands)
            return;

        var missing = FindCommandsWithoutValidator(registered, ServiceCollectionExtensions.GetKnownRequests(options));
        if (missing.Count == 0)
            return;

        const int maxListed = 20;
        string list = string.Join(Environment.NewLine, missing.Take(maxListed).Select(t => $"  - {t.FullName}"));
        if (missing.Count > maxListed)
            list += $"{Environment.NewLine}  ... e mais {missing.Count - maxListed}.";

        throw new InvalidOperationException(
            $"{missing.Count} command(s) sem validator (CqrsOptions.RequireValidatorForCommands está ativo). Crie um " +
            "AbstractValidator (pacote TEC.Cqrs.FluentValidation, com .AddFluentValidation()) ou um IRequestValidator ou, se o " +
            "command não recebe dados do usuário, marque-o com [SkipValidation]:" + Environment.NewLine + list);
    }

    /// <summary>Commands sem validador do tipo exato (inclusive genérico aberto que o container consegue fechar), em ordem alfabética.</summary>
    internal static IReadOnlyList<Type> FindCommandsWithoutValidator(IServiceCollection services, IEnumerable<Type> requests)
    {
        var validated = RegisteredServices.GetGenericArguments(services, typeof(IRequestValidator<>));
        var openValidators = RegisteredServices.GetOpenGenericImplementations(services, typeof(IRequestValidator<>));

        return [.. requests
            .Distinct()
            .Where(request => RequestMetadata.RequiresValidator(request)
                && !validated.Contains(request)
                && !openValidators.Any(open => RegisteredServices.CanClose(open, request)))
            .OrderBy(request => request.FullName, StringComparer.Ordinal)];
    }
}
