using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Core.Responses;
using TEC.Core.Responses.Pagination;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

/// <summary>Registro explícito (caminho compatível com Native AOT), sem varredura de assemblies.</summary>
public class ExplicitRegistrationTests
{
    private static ServiceProvider BuildExplicit()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CallLog>();

        services
            .AddTecCqrs(o => o
                .AddCommandHandler<CreateCustomerCommand, Guid, CreateCustomerHandler>()
                .AddQueryHandler<GetCustomerQuery, string, GetCustomerHandler>()
                .AddCommandHandler<CancelOrderCommand, CancelOrderHandler>()
                .AddRequestAuthorizer<CancelOrderCommand, CancelOrderAuthorizer>()
                .AddRequestAuthorizer<ICustomerOrder, CustomerOrderAuthorizer>()
                .AddNotificationHandler<CustomerUpdatedEvent, CustomerUpdatedHandler>()
                .AddNotificationHandler<ICustomerEvent, CustomerAuditHandler>())
            .AddFluentValidation(fv => fv.AddValidator<CreateCustomerCommand, CreateCustomerValidator>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Test]
    public async Task Explicitly_registered_command_and_query_execute_with_validation()
    {
        using var provider = BuildExplicit();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        var createdResult = await sender.Send(new CreateCustomerCommand("Maria", "12345678909"));
        var invalidResult = await sender.Send(new CreateCustomerCommand("", "1"));
        var notFoundResult = await sender.Send(new GetCustomerQuery(Guid.Empty));

        await Assert.That(createdResult.Value).IsEqualTo(CreateCustomerHandler.CreatedId);
        await Assert.That(invalidResult.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "NOME_OBRIGATORIO", "CPF_INVALIDO" }, CollectionOrdering.Matching);
        await Assert.That(notFoundResult.Error!.Type).IsEqualTo(ErrorType.NotFound); // Result<string> criado sem reflexão
    }

    [Test]
    public async Task Explicitly_registered_interface_authorizer_is_executed()
    {
        using var provider = BuildExplicit();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelOrderCommand(Guid.Empty));

        await Assert.That(result.Error!.Code).IsEqualTo("PEDIDO_DE_OUTRO_CLIENTE");
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "authorizer:concreto", "authorizer:interface" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Explicitly_registered_interface_notification_handler_receives_the_event()
    {
        using var provider = BuildExplicit();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new CustomerUpdatedEvent(Guid.NewGuid()));

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "atualizado", "auditoria" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Without_validation_package_command_without_validator_still_fails()
    {
        // Esquecer o AddFluentValidation não pode desligar a validação obrigatória em silêncio (fail closed)
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.AddCommandHandler<NoValidatorCommand, NoValidatorHandler>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var ex = await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new NoValidatorCommand()); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("SkipValidation");
    }

    [Test]
    public async Task AddFluentValidation_and_AddAspNetCore_cannot_be_called_twice()
    {
        var services = new ServiceCollection();
        var builder = services.AddTecCqrs(o => o.AddQueryHandler<GetCustomerQuery, string, GetCustomerHandler>())
            .AddFluentValidation(_ => { })
            .AddAspNetCore();

        await Assert.That(() => builder.AddFluentValidation(_ => { })).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => builder.AddAspNetCore()).ThrowsExactly<InvalidOperationException>();
    }
}

public class MetricsTests
{
    [Test]
    public async Task Requests_produce_counter_and_histogram_with_the_outcome()
    {
        using var provider = TestHost.Build(services: s => s.AddMetrics());
        var factory = provider.GetRequiredService<IMeterFactory>();
        List<(string Instrument, string? Unit, Dictionary<string, object?> Tags)> measurements = [];

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CqrsDiagnostics.MeterName && instrument.Meter.Scope == factory)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
                copy[tag.Key] = tag.Value;
            lock (measurements)
                measurements.Add((instrument.Name, instrument.Unit, copy));
        }
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
        listener.Start();

        using (var scope = provider.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await sender.Send(new CreateCustomerCommand("Maria", "12345678909"));
            await sender.Send(new DeactivateCustomerCommand(Guid.Empty));
            await Assert.That(async () => { await sender.Send(new FailCommand()); }).ThrowsExactly<InvalidOperationException>();
        }

        var counter = measurements.Where(m => m.Instrument == CqrsDiagnostics.RequestsMetricName).ToArray();
        var histogram = measurements.Where(m => m.Instrument == CqrsDiagnostics.RequestDurationMetricName).ToArray();

        await Assert.That(counter.Select(m => m.Tags[CqrsDiagnostics.OutcomeTag]))
            .IsEquivalentTo(new object?[] { "success", "failure", "exception" }, CollectionOrdering.Matching);
        await Assert.That(counter[0].Tags[CqrsDiagnostics.RequestTag]).IsEqualTo(typeof(CreateCustomerCommand).FullName);
        await Assert.That(counter[0].Tags[CqrsDiagnostics.KindTag]).IsEqualTo("command");
        await Assert.That(counter[0].Tags.ContainsKey(CqrsDiagnostics.ErrorTypeTag)).IsFalse();
        await Assert.That(counter[1].Tags[CqrsDiagnostics.ErrorTypeTag]).IsEqualTo(nameof(ErrorType.BusinessRule));
        await Assert.That(counter[2].Tags[CqrsDiagnostics.ErrorTypeTag]).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(histogram.Length).IsEqualTo(3);
        await Assert.That(histogram[0].Unit).IsEqualTo("s");
    }
}

public class AspNetCoreIntegrationTests
{
    private static List<IProducesResponseTypeMetadata> Metadata<TResult>()
        where TResult : IEndpointMetadataProvider
    {
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, RoutePatternFactory.Parse("/"), 0);
        TResult.PopulateMetadata(((Delegate)Metadata<TResult>).Method, builder);
        return [.. builder.Metadata.OfType<IProducesResponseTypeMetadata>()];
    }

    [Test]
    public async Task ApiResponseHttpResult_describes_success_and_failures_in_OpenAPI()
    {
        var metadata = Metadata<ApiResponseHttpResult<ApiResponse<string>>>();

        await Assert.That(metadata.Single(m => m.StatusCode == 200).Type).IsEqualTo(typeof(ApiResponse<string>));
        await Assert.That(metadata.Where(m => m.StatusCode >= 400).Select(m => m.StatusCode))
            .IsEquivalentTo(new[] { 400, 401, 403, 404, 409, 422, 429, 500, 502 }, CollectionOrdering.Matching);
        await Assert.That(metadata.Where(m => m.StatusCode >= 400).All(m => m.Type == typeof(ApiResponse))).IsTrue();
        await Assert.That(metadata.All(m => m.ContentTypes.SequenceEqual(["application/json"]))).IsTrue();
    }

    [Test]
    public async Task Created_and_paged_describe_their_own_envelope_in_OpenAPI()
    {
        await Assert.That(Metadata<ApiResponseCreatedHttpResult<Guid>>().Single(m => m.StatusCode == 201).Type)
            .IsEqualTo(typeof(ApiResponse<Guid>));
        await Assert.That(Metadata<ApiResponseHttpResult<PagedResponse<string>>>().Single(m => m.StatusCode == 200).Type)
            .IsEqualTo(typeof(PagedResponse<string>));
    }

    private static ServiceProvider BuildWithJsonContext() =>
        new ServiceCollection()
            .AddLogging()
            .AddTecCqrs(o => o.AddQueryHandler<GetCustomerQuery, string, GetCustomerHandler>())
            .AddAspNetCore(http => http.JsonTypeInfoResolver = TestJsonContext.Default)
            .Services
            .BuildServiceProvider();

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    [Test]
    public async Task JsonTypeInfoResolver_serializes_with_generated_metadata_and_TEC_Core_conventions()
    {
        using var provider = BuildWithJsonContext();

        var (status, body) = await ExecuteAsync(Result.Success("Maria").ToHttpResult(), provider);
        var (failureStatus, failure) = await ExecuteAsync(Result.Failure(Error.NotFound("X", "Não encontrado.")).ToHttpResult(), provider);

        await Assert.That(status).IsEqualTo(200);
        await Assert.That(body.GetProperty("data").GetString()).IsEqualTo("Maria");
        await Assert.That(body.TryGetProperty("traceId", out _)).IsFalse(); // nulos ignorados
        await Assert.That(failureStatus).IsEqualTo(404); // ApiResponse sem dados já vem incluído
        await Assert.That(failure.GetProperty("errors")[0].GetProperty("code").GetString()).IsEqualTo("X");
    }

    [Test]
    public async Task JsonTypeInfoResolver_without_the_type_throws_clear_exception()
    {
        using var provider = BuildWithJsonContext();

        var ex = await Assert.That(async () => { await ExecuteAsync(Result.Success(1).ToHttpResult(), provider); })
            .ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("JsonTypeInfoResolver");
    }

    private static async Task<(int Status, ListLoggerProvider Logs)> RunExceptionHandlerAsync(RequestDelegate endpoint)
    {
        var logs = new ListLoggerProvider();
        using var provider = TestHost.Build(services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.AddSingleton(new DiagnosticListener("TEC.Cqrs.Tests"));
            s.AddMetrics();
        });

        var app = new ApplicationBuilder(provider);
        app.UseTecExceptionHandler();
        app.Run(endpoint);
        var pipeline = app.Build();

        using var scope = provider.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        await pipeline(context);
        return (context.Response.StatusCode, logs);
    }

    [Test]
    public async Task UseTecExceptionHandler_does_not_log_pipeline_exception_again()
    {
        // .NET 10: SuppressDiagnosticsCallback; .NET 8: middleware próprio com o mesmo comportamento
        var (status, logs) = await RunExceptionHandlerAsync(async context =>
            await context.RequestServices.GetRequiredService<ISender>().Send(new FailCommand()));

        await Assert.That(status).IsEqualTo(500);
        await Assert.That(logs.Entries.Count(e => e.Level >= LogLevel.Error)).IsEqualTo(1);
        await Assert.That(logs.Entries.Single(e => e.Level >= LogLevel.Error).EventId.Id).IsEqualTo(1004);
    }

    [Test]
    public async Task UseTecExceptionHandler_does_not_log_client_error_and_logs_unknown_exception()
    {
        var (notFound, clientLogs) = await RunExceptionHandlerAsync(_ => throw new NotFoundException("X", "Não encontrado."));
        var (failure, serverLogs) = await RunExceptionHandlerAsync(_ => throw new InvalidOperationException("bug"));

        await Assert.That(notFound).IsEqualTo(404);
        await Assert.That(clientLogs.Entries.Any(e => e.Level >= LogLevel.Error)).IsFalse();
        await Assert.That(failure).IsEqualTo(500);
        await Assert.That(serverLogs.Entries.Count(e => e.Level >= LogLevel.Error && e.Exception is InvalidOperationException)).IsEqualTo(1);
    }

    [Test]
    public async Task Paged_failure_has_neither_data_nor_pagination()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var result = Result.Failure<PagedResult<string>>(Error.NotFound("LISTA", "Nada.")).ToHttpResult();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);

        await Assert.That(context.Response.StatusCode).IsEqualTo(404);
        await Assert.That(result.Value).IsTypeOf<PagedResponse<string>>();
        await Assert.That(document.RootElement.TryGetProperty("data", out _)).IsFalse();
        await Assert.That(document.RootElement.TryGetProperty("pagination", out _)).IsFalse();
        await Assert.That(document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString()).IsEqualTo("LISTA");
    }
}

[JsonSerializable(typeof(ApiResponse<string>))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
