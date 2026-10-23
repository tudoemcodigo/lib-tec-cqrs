using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Tests.Fakes;

namespace TEC.Cqrs.Tests;

/// <summary>Monta o container como um consumidor faria (varrendo o assembly de testes, com os três pacotes).</summary>
internal static class TestHost
{
    public static ServiceProvider Build(Action<CqrsOptions>? configure = null, bool withUnitOfWork = false,
        Action<IServiceCollection>? services = null, Action<CqrsFluentValidationOptions>? validation = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddScoped<CallLog>();

        if (withUnitOfWork)
        {
            collection.AddScoped<FakeUnitOfWork>();
            collection.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FakeUnitOfWork>());
        }

        collection
            .AddTecCqrs(options =>
            {
                options.RegisterServicesFromAssemblyContaining<CallLog>();
                configure?.Invoke(options);
            })
            .AddFluentValidation(options =>
            {
                options.RegisterValidatorsFromAssemblyContaining<CallLog>();
                validation?.Invoke(options);
            })
            .AddAspNetCore();

        services?.Invoke(collection);

        return collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
