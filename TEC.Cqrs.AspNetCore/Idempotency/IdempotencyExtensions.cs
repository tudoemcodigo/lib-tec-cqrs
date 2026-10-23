using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Idempotency;

namespace TEC.Cqrs.AspNetCore.Idempotency;

/// <summary>Registro e middleware da idempotência HTTP.</summary>
public static class IdempotencyExtensions
{
    /// <summary>
    /// Configura a idempotência HTTP por <c>Idempotency-Key</c>. Também é preciso um <see cref="IIdempotencyStore"/>
    /// (<c>services.AddInMemoryIdempotencyStore()</c> ou o <c>AddTecOrmIdempotency&lt;TContext&gt;()</c> do TEC.ORM),
    /// o middleware <see cref="UseTecIdempotency"/> e marcar os endpoints com <c>.WithIdempotency()</c>.
    /// </summary>
    /// <param name="builder">Builder do <c>AddTecCqrs</c>.</param>
    /// <param name="configure">Opções (validadas na inicialização).</param>
    /// <returns>O próprio builder.</returns>
    /// <exception cref="InvalidOperationException">Chamado mais de uma vez.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining&lt;Program&gt;())
    ///     .AddAspNetCore()
    ///     .AddIdempotency(o => o.Retention = TimeSpan.FromHours(12));
    /// builder.Services.AddInMemoryIdempotencyStore();
    /// </code>
    /// </example>
    public static ICqrsBuilder AddIdempotency(this ICqrsBuilder builder, Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var services = builder.Services;
        if (services.Any(d => d.ServiceType == typeof(IdempotencyMarker)))
            throw new InvalidOperationException("AddIdempotency já foi chamado. Configure tudo em uma única chamada.");

        services.AddSingleton<IdempotencyMarker>();
        var optionsBuilder = services.AddOptions<IdempotencyOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);
        services.AddSingleton<IValidateOptions<IdempotencyOptions>, IdempotencyOptionsValidator>();
        optionsBuilder.ValidateOnStart();
        return builder;
    }

    /// <summary>
    /// Adiciona o middleware de idempotência. Registre depois de <c>UseAuthentication</c>/<c>UseAuthorization</c> (o escopo
    /// da chave é o usuário) e antes dos endpoints. Só os endpoints marcados com <c>.WithIdempotency()</c> ou
    /// <see cref="IdempotentAttribute"/> são tratados.
    /// </summary>
    /// <param name="app">Pipeline da aplicação.</param>
    /// <returns>O próprio <paramref name="app"/>.</returns>
    /// <exception cref="InvalidOperationException"><c>AddIdempotency</c> não chamado ou nenhum <see cref="IIdempotencyStore"/> registrado.</exception>
    /// <example>
    /// <code>
    /// app.UseAuthentication();
    /// app.UseAuthorization();
    /// app.UseTecIdempotency();
    /// </code>
    /// </example>
    public static IApplicationBuilder UseTecIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.ApplicationServices;
        if (services.GetService<IdempotencyMarker>() is null)
            throw new InvalidOperationException("Chame .AddIdempotency() no AddTecCqrs(...) antes de usar o UseTecIdempotency().");

        using (var scope = services.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IIdempotencyStore>() is null)
            {
                throw new InvalidOperationException(
                    "Nenhum IIdempotencyStore registrado. Use services.AddInMemoryIdempotencyStore() (uma instância) ou " +
                    "services.AddTecOrmIdempotency<TContext>() do TEC.ORM (banco compartilhado).");
            }
        }

        return app.UseMiddleware<IdempotencyMiddleware>();
    }

    private sealed class IdempotencyMarker;

    private sealed class IdempotencyOptionsValidator : IValidateOptions<IdempotencyOptions>
    {
        public ValidateOptionsResult Validate(string? name, IdempotencyOptions options)
        {
            var errors = options.Validate().ToList();
            return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
