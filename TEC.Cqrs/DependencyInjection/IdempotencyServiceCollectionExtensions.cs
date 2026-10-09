using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TEC.Cqrs.Idempotency;

namespace TEC.Cqrs.DependencyInjection;

/// <summary>Registro dos stores de idempotência.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registra o <see cref="InMemoryIdempotencyStore"/> como <see cref="IIdempotencyStore"/> (Singleton), para testes,
    /// desenvolvimento e aplicações de uma única instância. Não substitui um store registrado antes.
    /// </summary>
    /// <param name="services">Container.</param>
    /// <param name="configure">Opções (limite de entradas).</param>
    /// <returns>O próprio <paramref name="services"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Limite de entradas menor que 1.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddInMemoryIdempotencyStore(o => o.MaxEntries = 50_000);
    /// </code>
    /// </example>
    public static IServiceCollection AddInMemoryIdempotencyStore(this IServiceCollection services,
        Action<InMemoryIdempotencyStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new InMemoryIdempotencyStoreOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxEntries, 1, nameof(configure));

        services.TryAddSingleton<IIdempotencyStore>(sp => new InMemoryIdempotencyStore(options, sp.GetService<TimeProvider>()));
        return services;
    }
}
