using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.Diagnostics;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.FluentValidation;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Tests.Fakes;
using TUnit.Assertions.Enums;

namespace TEC.Cqrs.Tests;

public class ValidationRegressionTests
{
    [Test]
    public async Task Dois_validators_nao_duplicam_erros()
    {
        // Antes: o mesmo ValidationContext acumulava as falhas e o segundo validator devolvia também as do primeiro
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new DoisValidatorsCommand("", ""));

        await Assert.That(result.Errors.Select(e => e.Code))
            .IsEquivalentTo(new[] { "CPF_OBRIGATORIO", "NOME_OBRIGATORIO" }, CollectionOrdering.Any);
        await Assert.That(result.Errors.Count).IsEqualTo(2);
    }
}

public class PolymorphicNotificationTests
{
    [Test]
    public async Task Handlers_de_interface_recebem_o_evento_concreto_uma_unica_vez()
    {
        using var provider = TestHost.Build();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new ClienteAtualizadoEvent(Guid.NewGuid()));

        // Tipo concreto primeiro (por nome do handler), depois a interface; o HandlerDuplo roda só pelo tipo concreto
        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "atualizado", "duplo:concreto", "auditoria" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Handlers_de_interface_recebem_o_evento_concreto_apos_o_commit()
    {
        using var provider = TestHost.Build(withUnitOfWork: true, services: s =>
            s.AddScoped<INotificationHandler<EventoComHandlerQuebrado>, NoOpHandler>());
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublicarEventoQuebradoCommand());

        await Assert.That(scope.ServiceProvider.GetRequiredService<CallLog>().Entries)
            .IsEquivalentTo(new[] { "begin", "commit", "auditoria", "duplo:interface" }, CollectionOrdering.Matching);
    }

    private sealed class NoOpHandler : INotificationHandler<EventoComHandlerQuebrado>
    {
        public Task Handle(EventoComHandlerQuebrado notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public class OptionsRegressionTests
{
    [Test]
    public async Task Opcoes_nao_podem_ser_alteradas_depois_do_AddTecCqrs()
    {
        using var provider = TestHost.Build();
        var options = provider.GetRequiredService<CqrsOptions>();

        await Assert.That(() => options.RequireAuthorization = false).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.RequireValidatorForCommands = false).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.SlowRequestThreshold = null).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.RecordExceptionDetailsInTraces = true).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.RegisterServicesFromAssemblyContaining<CallLog>()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.AddBehavior(typeof(RegistroBehavior<,>))).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => options.AddCommandHandler<InativarClienteCommand, InativarClienteHandler>()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(options.RequireAuthorization).IsTrue();

        var validation = provider.GetRequiredService<CqrsFluentValidationOptions>();
        await Assert.That(() => validation.CamelCaseValidationFields = false).ThrowsExactly<InvalidOperationException>();
        await Assert.That(() => validation.AddValidator<CriarClienteCommand, CriarClienteValidator>()).ThrowsExactly<InvalidOperationException>();
    }

    [Test]
    public async Task Opcoes_podem_ser_alteradas_antes_do_AddTecCqrs()
    {
        var options = new CqrsOptions { RequireAuthorization = false, SlowRequestThreshold = null };

        await Assert.That(options.RequireAuthorization).IsFalse();
        await Assert.That(options.SlowRequestThreshold).IsNull();
    }
}

public class LoggingRegressionTests
{
    private static (ServiceProvider Provider, ListLoggerProvider Logs) Build(Action<CqrsOptions>? configure = null)
    {
        var logs = new ListLoggerProvider();
        var provider = TestHost.Build(configure, services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
        });
        return (provider, logs);
    }

    private static IEnumerable<ListLoggerProvider.LogEntry> Pipeline(ListLoggerProvider logs) =>
        logs.Entries.Where(e => e.Category == typeof(Mediator).FullName);

    [Test]
    public async Task AppException_interna_gera_um_unico_log_de_erro_com_a_excecao()
    {
        // Antes: ExceptionBehavior e LoggingBehavior registravam a mesma falha (dois logs de erro)
        var (provider, logs) = Build();
        using (provider)
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new IntegrarCommand());

        var errors = Pipeline(logs).Where(e => e.Level >= LogLevel.Error).ToArray();
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Exception).IsTypeOf<TEC.Core.Exceptions.IntegrationException>();
    }

    [Test]
    public async Task Excecao_em_command_aninhado_e_registrada_uma_vez_e_nao_de_novo_no_exception_handler()
    {
        var (provider, logs) = Build();
        Exception? thrown = null;
        using (provider)
        using (var scope = provider.CreateScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PaiDeFalharCommand());
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
        }

        await Assert.That(Pipeline(logs).Count(e => e.EventId.Id == 1004)).IsEqualTo(1);
        await Assert.That(ApplicationBuilderExtensions.SuppressDiagnostics(thrown)).IsTrue();
        await Assert.That(ApplicationBuilderExtensions.SuppressDiagnostics(new InvalidOperationException("não registrada"))).IsFalse();
    }

    [Test]
    public async Task Excecao_nao_e_marcada_como_registrada_quando_o_log_de_erro_esta_desabilitado()
    {
        // Antes: a exceção era marcada mesmo sem o log ter sido escrito, e o UseTecExceptionHandler não a registrava:
        // a falha ficava sem nenhum registro
        var logs = new ListLoggerProvider();
        var provider = TestHost.Build(services: s =>
        {
            s.AddSingleton<ILoggerProvider>(logs);
            s.AddLogging(b => b.AddFilter(typeof(Mediator).FullName, LogLevel.None));
        });
        Exception? thrown = null;
        using (provider)
        using (var scope = provider.CreateScope())
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PaiDeFalharCommand());
            }
            catch (InvalidOperationException ex)
            {
                thrown = ex;
            }
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(Pipeline(logs)).IsEmpty();
        await Assert.That(CqrsDiagnostics.IsExceptionLogged(thrown)).IsFalse();
        await Assert.That(ApplicationBuilderExtensions.SuppressDiagnostics(thrown)).IsFalse();
    }

    [Test]
    public async Task Cancelamento_gera_log_em_nivel_baixo_e_marca_a_activity()
    {
        List<Activity> stopped = [];
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == nameof(CancelavelQuery))
                {
                    lock (stopped)
                        stopped.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var (provider, logs) = Build();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using (provider)
        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CancelavelQuery(), cts.Token); })
                .Throws<OperationCanceledException>();
        }

        var entries = Pipeline(logs).ToArray();
        await Assert.That(entries.Any(e => e.EventId.Id == 1005 && e.Level == LogLevel.Debug)).IsTrue();
        await Assert.That(entries.Any(e => e.Level >= LogLevel.Warning)).IsFalse();

        Activity activity;
        lock (stopped)
            activity = stopped.Single();
        await Assert.That(activity.GetTagItem("cqrs.canceled") is true).IsTrue();
        await Assert.That(activity.GetTagItem("cqrs.success") is false).IsTrue();
        await Assert.That(activity.Status).IsNotEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Performance_mede_tambem_execucoes_que_terminam_com_excecao()
    {
        var (provider, logs) = Build(o => o.SlowRequestThreshold = TimeSpan.FromMilliseconds(1));
        using (provider)
        using (var scope = provider.CreateScope())
        {
            await Assert.That(async () => { await scope.ServiceProvider.GetRequiredService<ISender>().Send(new LentoComExcecaoCommand()); })
                .ThrowsExactly<TimeoutException>();
        }

        await Assert.That(Pipeline(logs).Count(e => e.EventId.Id == 1006)).IsEqualTo(1);
    }
}

public class TypeScannerTests
{
    [Test]
    public async Task Tipos_que_nao_carregam_geram_erro_claro_em_vez_de_serem_ignorados()
    {
        var assembly = new AssemblyComTiposQuebrados();

        var ex = await Assert.That(() => TypeScanner.GetConcreteTypes(assembly).ToArray()).ThrowsExactly<InvalidOperationException>();

        await Assert.That(ex!.Message).Contains("Dependencia.Ausente");
        await Assert.That(ex.InnerException).IsTypeOf<ReflectionTypeLoadException>();
    }

    [Test]
    public async Task Tipos_sao_ordenados_pelo_nome_completo()
    {
        var names = TypeScanner.GetConcreteTypes(typeof(CallLog).Assembly).Select(t => t.FullName).ToArray();

        await Assert.That(names).IsEquivalentTo(names.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    private sealed class AssemblyComTiposQuebrados : Assembly
    {
        public override string FullName => "Quebrado, Version=1.0.0.0";

        public override Type[] GetTypes() => throw new ReflectionTypeLoadException(
            [typeof(CallLog), null],
            [new FileNotFoundException("Could not load file or assembly 'Dependencia.Ausente'.")]);
    }
}
