using Microsoft.Extensions.DependencyInjection;

namespace TEC.Cqrs.DependencyInjection;

/// <summary>
/// Retorno do <c>AddTecCqrs</c>: permite encadear as extensões dos outros pacotes
/// (<c>.AddFluentValidation()</c> do <c>TEC.Cqrs.FluentValidation</c>, <c>.AddAspNetCore()</c> do <c>TEC.Cqrs.AspNetCore</c>).
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddTecCqrs(options => options.RegisterServicesFromAssemblyContaining&lt;Program&gt;())
///     .AddFluentValidation()
///     .AddAspNetCore();
/// </code>
/// </example>
public interface ICqrsBuilder
{
    /// <summary>Container em configuração.</summary>
    IServiceCollection Services { get; }

    /// <summary>Opções já validadas e congeladas pelo <c>AddTecCqrs</c> (somente leitura).</summary>
    CqrsOptions Options { get; }
}

internal sealed class CqrsBuilder(IServiceCollection services, CqrsOptions options) : ICqrsBuilder
{
    public IServiceCollection Services { get; } = services;

    public CqrsOptions Options { get; } = options;
}
