using Microsoft.Extensions.DependencyInjection;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class MediatorTests
{
    [Test]
    public async Task Send_command_returns_handler_value()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var result = await sender.Send(new CreateCustomerCommand("Maria", "12345678909"));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(CreateCustomerHandler.CreatedId);
    }

    [Test]
    public async Task Send_query_returns_handler_value()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetCustomerQuery(Guid.NewGuid()));

        await Assert.That(result.Value).IsEqualTo("Maria");
    }

    [Test]
    public async Task Validation_error_stops_the_pipeline_without_calling_the_handler()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("", "123"));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Errors.Select(e => e.Type))
            .IsEquivalentTo(new[] { ErrorType.Validation, ErrorType.Validation }, CollectionOrdering.Matching);
        await Assert.That(result.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "NOME_OBRIGATORIO", "CPF_INVALIDO" }, CollectionOrdering.Matching);
        await Assert.That(result.Errors.Select(e => e.Field))
            .IsEquivalentTo(new string?[] { "name", "cpf" }, CollectionOrdering.Matching);
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries).DoesNotContain("handler");
    }

    [Test]
    public async Task Validation_keeps_original_field_name_when_camelCase_is_disabled()
    {
        using var provider = TestHost.Build(validation: o => o.CamelCaseValidationFields = false);
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("", "12345678909"));

        await Assert.That(result.Errors.Count).IsEqualTo(1);
        await Assert.That(result.Errors[0].Field).IsEqualTo("Name");
    }

    [Test]
    public async Task Command_without_value_propagates_handler_failure()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new DeactivateCustomerCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("CLIENTE_JA_INATIVO");
        await Assert.That(result.Error.Type).IsEqualTo(ErrorType.BusinessRule);
    }

    [Test]
    public async Task AppException_thrown_in_handler_becomes_failure_Result()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetCustomerQuery(Guid.Empty));

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error!.Code).IsEqualTo("CLIENTE_NAO_ENCONTRADO");
        await Assert.That(result.Error.Type).IsEqualTo(ErrorType.NotFound);
    }

    [Test]
    public async Task IntegrationException_becomes_external_service_failure()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new IntegrateCommand());

        await Assert.That(result.Error!.Type).IsEqualTo(ErrorType.ExternalService);
    }

    [Test]
    public async Task Unhandled_exception_is_propagated()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FailCommand()); })
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Request_without_handler_throws_clear_exception()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoHandlerQuery()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(NoHandlerQuery));
    }

    [Test]
    public async Task Custom_behavior_wraps_the_handler()
    {
        using var provider = TestHost.Build(o => o.AddBehavior(typeof(RecordingBehavior<,>)));
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateCustomerCommand("Maria", "12345678909"));

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "behavior:antes", "handler", "behavior:depois" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Publish_executes_all_handlers_in_order()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new CustomerCreatedEvent(Guid.NewGuid()));

        // Ordem determinística: nome completo do handler (AtualizarCrmHandler < EnviarEmailHandler)
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "crm", "email" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Publish_uses_the_runtime_type_of_the_notification()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();
        INotification notification = new CustomerCreatedEvent(Guid.NewGuid());

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(notification);

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Publish_without_handlers_does_not_fail()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await Assert.That(() => scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new NoHandlersEvent()))
            .ThrowsNothing();
    }

    private sealed record NoHandlersEvent : INotification;
}

public class RegistrationTests
{
    [Test]
    public async Task AddTecCqrs_without_assembly_throws()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddTecCqrs(_ => { })).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Handler_registered_directly_in_container_with_another_handler_fails_in_AddTecCqrs()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestHandler<DeactivateCustomerCommand, Result>, DuplicateHandler<int>>();

        var ex = await Assert.That(() => services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(DeactivateCustomerCommand));
    }

    [Test]
    public async Task AddTecCqrs_called_twice_throws()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>());

        await Assert.That(() => services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()))
            .ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Two_handlers_for_same_request_fail_at_registration()
    {
        var options = new CqrsOptions();

        var ex = await Assert.That(() => options.RegisterTypes([typeof(DeactivateCustomerHandler), typeof(DuplicateHandler<int>)]))
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains(nameof(DeactivateCustomerCommand));
    }

    [Test]
    public async Task Same_handler_registered_twice_is_ignored()
    {
        var services = new ServiceCollection();
        var options = new CqrsOptions();

        options.RegisterTypes([typeof(DeactivateCustomerHandler), typeof(DeactivateCustomerHandler)]);
        ServiceCollectionExtensions.ApplyRegistrations(services, options);

        await Assert.That(services.Count(d => d.ServiceType == typeof(IRequestHandler<DeactivateCustomerCommand, Result>))).IsEqualTo(1);
    }

    [Test]
    public async Task AddBehavior_rejects_type_that_is_not_an_open_generic_behavior()
    {
        var options = new CqrsOptions();

        await Assert.That(() => options.AddBehavior(typeof(string))).Throws<ArgumentException>();
        await Assert.That(() => options.AddBehavior(typeof(RecordingBehavior<CreateCustomerCommand, Result<Guid>>))).Throws<ArgumentException>();
    }

    [Test]
    public async Task SlowRequestThreshold_rejects_non_positive_value()
    {
        var options = new CqrsOptions();

        await Assert.That(() => options.SlowRequestThreshold = TimeSpan.Zero).ThrowsExactly<ArgumentOutOfRangeException>();
        options.SlowRequestThreshold = null;
        await Assert.That(options.SlowRequestThreshold).IsNull();
    }

    [Test]
    public async Task FindRequestsWithoutHandler_reports_requests_without_handler()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>());

        var missing = CqrsDiagnostics.FindRequestsWithoutHandler(services, typeof(CallLog).Assembly);

        await Assert.That(missing).IsEquivalentTo(new[] { typeof(NoHandlerQuery) }, CollectionOrdering.Matching);
    }


    /// <summary>Genérico aberto: ignorado na varredura; fechado apenas neste teste para simular duplicidade.</summary>
    private sealed class DuplicateHandler<T> : ICommandHandler<DeactivateCustomerCommand>
    {
        public Task<Result> Handle(DeactivateCustomerCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }
}
