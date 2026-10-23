using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.FluentValidation.Internal;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.DependencyInjection;

/// <summary>Validação do pipeline do TEC.Cqrs com FluentValidation.</summary>
public static class CqrsBuilderFluentValidationExtensions
{
    /// <summary>
    /// Registra os validators do FluentValidation encontrados nos assemblies informados ao <c>AddTecCqrs</c>
    /// (<c>RegisterServicesFromAssembly</c>) e liga-os ao pipeline.
    /// </summary>
    /// <remarks>
    /// <para>Usa reflexão: em apps com trimming ou Native AOT, use
    /// <see cref="AddFluentValidation(ICqrsBuilder, Action{CqrsFluentValidationOptions})"/> com <c>AddValidator</c>.</para>
    /// <para>Também converte a <c>FluentValidation.ValidationException</c> lançada nos handlers (<c>ValidateAndThrow</c>)
    /// em falha de validação, no pipeline e no <c>UseTecExceptionHandler</c>.</para>
    /// <para><b>Dados sensíveis:</b> a mensagem de cada falha vai para o cliente (HTTP 400). Os templates padrão do
    /// FluentValidation aceitam os placeholders <c>{PropertyValue}</c> e <c>{ComparisonValue}</c>, que ecoam o valor
    /// recebido: em campos sensíveis (senha, token, documento), use <c>WithMessage</c> com texto fixo, sem placeholders
    /// de valor.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Chamado mais de uma vez, ou requisição sem validator próprio cujo único validator é de um tipo base ou interface
    /// (não é executado pelo pipeline; use <c>Include</c> em um validator do tipo exato).
    /// </exception>
    /// <example>
    /// <code>
    /// builder.Services.AddTecCqrs(options => options.RegisterServicesFromAssemblyContaining&lt;Program&gt;())
    ///     .AddFluentValidation();
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(ValidatorScanner.ScanningMessage)]
    [RequiresDynamicCode(ValidatorScanner.DynamicCodeMessage)]
    public static ICqrsBuilder AddFluentValidation(this ICqrsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddFluentValidation(options =>
        {
            foreach (var assembly in builder.Options.Assemblies)
                options.RegisterValidatorsFromAssembly(assembly);
        });
    }

    /// <summary>
    /// Registra os validators configurados em <paramref name="configure"/> e liga-os ao pipeline (compatível com Native AOT
    /// quando os validators são registrados com <c>AddValidator</c>).
    /// </summary>
    /// <remarks>
    /// <para>Os validators são executados pelo behavior de validação do <c>TEC.Cqrs</c> (depois da autorização e antes do
    /// handler). Havendo erros, o handler não é chamado e a resposta é falha <c>Validation</c> (HTTP 400) com um erro por
    /// campo. Todos os validators da requisição são executados, cada um com o próprio contexto.</para>
    /// <para>Validators de tipo base ou interface (<c>AbstractValidator&lt;IPossuiCpf&gt;</c>) <b>não</b> são executados:
    /// reaproveite as regras com <c>Include</c> em um validator do tipo exato. Uma requisição cujo único validator é de tipo
    /// base/interface faz este método falhar na inicialização.</para>
    /// <para><b>Dados sensíveis:</b> as mensagens das falhas são devolvidas ao cliente como vieram do FluentValidation.
    /// Os placeholders <c>{PropertyValue}</c> e <c>{ComparisonValue}</c> ecoam o valor recebido (ou o valor de comparação):
    /// em campos sensíveis (senha, token, documento), use <c>WithMessage</c> com texto fixo, sem placeholders de valor.</para>
    /// </remarks>
    /// <inheritdoc cref="AddFluentValidation(ICqrsBuilder)" path="/exception"/>
    /// <example>
    /// <code>
    /// builder.Services.AddTecCqrs(options => options.AddCommandHandler&lt;CriarClienteCommand, Guid, CriarClienteHandler&gt;())
    ///     .AddFluentValidation(fv => fv.AddValidator&lt;CriarClienteCommand, CriarClienteValidator&gt;());
    /// </code>
    /// </example>
    public static ICqrsBuilder AddFluentValidation(this ICqrsBuilder builder, Action<CqrsFluentValidationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(CqrsFluentValidationOptions)))
            throw new InvalidOperationException("AddFluentValidation já foi chamado. Configure tudo em uma única chamada.");

        var options = new CqrsFluentValidationOptions();
        configure(options);

        services.AddSingleton(options);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionErrorMapper, FluentValidationExceptionMapper>());

        foreach (var descriptor in options.Validators)
            services.TryAddEnumerable(descriptor);
        foreach (var descriptor in options.Adapters)
            services.TryAddEnumerable(descriptor);

        EnsureValidatorsAreApplied(services, options.ScannedRequests);

        options.Freeze();
        return builder;
    }

    /// <summary>
    /// Falha se uma requisição sem validator próprio tiver validator apenas para um tipo base ou interface
    /// (ex.: <c>AbstractValidator&lt;IPossuiCpf&gt;</c>): esse validator não é executado pelo pipeline, e a requisição
    /// rodaria sem a validação esperada.
    /// </summary>
    /// <param name="services">Container (handlers e validators já registrados).</param>
    /// <param name="additionalRequests">Requisições além das que têm handler registrado (ex.: encontradas na varredura).</param>
    internal static void EnsureValidatorsAreApplied(IServiceCollection services, IEnumerable<Type> additionalRequests)
    {
        var validatorTargets = RegisteredServices.GetGenericArguments(services, typeof(global::FluentValidation.IValidator<>));
        if (validatorTargets.Count == 0)
            return;

        var requests = RegisteredServices.GetRequests(services, additionalRequests);
        var applied = RegisteredServices.GetGenericArguments(services, typeof(IRequestValidator<>));

        foreach (var request in requests.OrderBy(r => r.FullName, StringComparer.Ordinal))
        {
            if (validatorTargets.Contains(request) || applied.Contains(request))
                continue;

            var target = validatorTargets
                .Where(t => t != request && t.IsAssignableFrom(request))
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (target is null)
                continue;

            string validator = RegisteredServices.FindImplementationName(services, typeof(global::FluentValidation.IValidator<>), target)
                ?? $"AbstractValidator<{target.Name}>";

            throw new InvalidOperationException(
                $"A requisição '{request.FullName}' não possui validator próprio, mas '{validator}' valida '{target.Name}' " +
                $"(tipo base ou interface), que não é aplicado automaticamente. Crie um AbstractValidator<{request.Name}> " +
                $"e use Include(new {StripArity(validator)}()) dentro dele.");
        }
    }

    private static string StripArity(string name) => name.IndexOf('`', StringComparison.Ordinal) is var i and >= 0 ? name[..i] : name;
}
