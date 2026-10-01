using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.FluentValidation.Internal;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.FluentValidation;

/// <summary>Configuração do <c>AddFluentValidation</c>.</summary>
/// <remarks>
/// Congelada ao final do <c>AddFluentValidation</c>: alterá-la depois lança <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class CqrsFluentValidationOptions
{
    private readonly List<ServiceDescriptor> _validators = [];
    private readonly List<ServiceDescriptor> _adapters = [];
    private readonly List<Type> _scannedRequests = [];
    private readonly List<Assembly> _assemblies = [];
    private bool _frozen;

    internal IReadOnlyList<ServiceDescriptor> Validators => _validators;

    internal IReadOnlyList<ServiceDescriptor> Adapters => _adapters;

    internal IReadOnlyList<Type> ScannedRequests => _scannedRequests;

    /// <summary>
    /// Converte o nome do campo dos erros de validação para camelCase ("Endereco.Cep" → "endereco.cep"),
    /// igual ao JSON da API. Padrão: <c>true</c>.
    /// </summary>
    public bool CamelCaseValidationFields
    {
        get;
        set
        {
            EnsureNotFrozen();
            field = value;
        }
    } = true;

    /// <summary>
    /// Registra um validator do FluentValidation para a requisição (compatível com Native AOT). Vários validators para a
    /// mesma requisição são todos executados.
    /// </summary>
    /// <typeparam name="TRequest">Tipo <b>exato</b> da requisição (validators de tipo base ou interface não são executados: use <c>Include</c>).</typeparam>
    /// <typeparam name="TValidator">Validator (ex.: <c>AbstractValidator&lt;TRequest&gt;</c>).</typeparam>
    /// <remarks>
    /// As mensagens das falhas vão para o cliente (HTTP 400). Em campos sensíveis (senha, token), use <c>WithMessage</c>
    /// com texto fixo: os placeholders <c>{PropertyValue}</c> e <c>{ComparisonValue}</c> ecoam o valor recebido.
    /// </remarks>
    /// <example>
    /// <code>
    /// .AddFluentValidation(fv => fv.AddValidator&lt;CriarClienteCommand, CriarClienteValidator&gt;())
    /// </code>
    /// </example>
    public CqrsFluentValidationOptions AddValidator<TRequest,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TValidator>()
        where TRequest : IBaseRequest
        where TValidator : class, global::FluentValidation.IValidator<TRequest>
    {
        EnsureNotFrozen();
        _validators.Add(ServiceDescriptor.Scoped<global::FluentValidation.IValidator<TRequest>, TValidator>());
        _adapters.Add(ServiceDescriptor.Scoped<IRequestValidator<TRequest>, FluentValidationRequestValidator<TRequest>>());
        return this;
    }

    /// <summary>Registra os validators do FluentValidation (<c>IValidator&lt;T&gt;</c>) encontrados no assembly.</summary>
    /// <remarks>Usa reflexão: em apps com trimming ou Native AOT, use <see cref="AddValidator{TRequest, TValidator}"/>.</remarks>
    [RequiresUnreferencedCode(ValidatorScanner.ScanningMessage)]
    [RequiresDynamicCode(ValidatorScanner.DynamicCodeMessage)]
    public CqrsFluentValidationOptions RegisterValidatorsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureNotFrozen();
        if (_assemblies.Contains(assembly))
            return this;

        RegisterTypes(TypeScanner.GetConcreteTypes(assembly));
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>Registra os validators do FluentValidation do assembly que contém <typeparamref name="T"/>.</summary>
    /// <inheritdoc cref="RegisterValidatorsFromAssembly" path="/remarks"/>
    [RequiresUnreferencedCode(ValidatorScanner.ScanningMessage)]
    [RequiresDynamicCode(ValidatorScanner.DynamicCodeMessage)]
    public CqrsFluentValidationOptions RegisterValidatorsFromAssemblyContaining<T>() => RegisterValidatorsFromAssembly(typeof(T).Assembly);

    /// <summary>Registra os validators encontrados (e guarda as requisições, para a verificação de inicialização).</summary>
    [RequiresUnreferencedCode(ValidatorScanner.ScanningMessage)]
    [RequiresDynamicCode(ValidatorScanner.DynamicCodeMessage)]
    internal void RegisterTypes(IEnumerable<Type> types)
    {
        EnsureNotFrozen();
        foreach (var type in types)
        {
            if (typeof(IBaseRequest).IsAssignableFrom(type) && !type.ContainsGenericParameters && !_scannedRequests.Contains(type))
                _scannedRequests.Add(type);

            foreach (var service in TypeScanner.GetClosedInterfaces(type, typeof(global::FluentValidation.IValidator<>)))
            {
                var target = service.GetGenericArguments()[0];
                _validators.Add(ServiceDescriptor.Scoped(service, type));

                // Só requisições têm adaptador para o pipeline (validators de outros tipos, ex.: DTOs aninhados, são
                // usados via SetValidator/Include)
                if (typeof(IBaseRequest).IsAssignableFrom(target))
                {
                    _adapters.Add(ServiceDescriptor.Scoped(
                        typeof(IRequestValidator<>).MakeGenericType(target),
                        typeof(FluentValidationRequestValidator<>).MakeGenericType(target)));
                }
            }
        }
    }

    internal void Freeze() => _frozen = true;

    private void EnsureNotFrozen()
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "As opções do TEC.Cqrs.FluentValidation não podem ser alteradas depois do AddFluentValidation. " +
                "Configure tudo dentro de AddFluentValidation(options => ...).");
        }
    }
}
