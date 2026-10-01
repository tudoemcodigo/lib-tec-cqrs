using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Internal;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Diagnostics;

/// <summary>Rastreamento e métricas (OpenTelemetry) e verificações de registro do CQRS.</summary>
public static class CqrsDiagnostics
{
    /// <summary>
    /// Nome do <see cref="System.Diagnostics.ActivitySource"/> do pipeline. Cada requisição gera uma
    /// <c>Activity</c> com as tags <c>cqrs.request</c>, <c>cqrs.kind</c>, <c>cqrs.outcome</c>, <c>cqrs.success</c>,
    /// <c>cqrs.error_code</c> e, em falha ou exceção, <c>error.type</c>; cada publicação de notificação gera uma com
    /// <c>cqrs.notification</c>, <c>cqrs.kind</c> = <c>notification</c>, <c>cqrs.after_commit</c> e <c>cqrs.outcome</c>.
    /// Em exceção, o evento <c>exception</c> leva só <c>exception.type</c> (mensagem e stack trace apenas com
    /// <c>CqrsOptions.RecordExceptionDetailsInTraces</c>).
    /// Exportado sem configuração pelo <c>AddEnterpriseObservability</c> do TEC.Observability (prefixo <c>TEC.*</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// // Sem o TEC.Observability:
    /// builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource(CqrsDiagnostics.ActivitySourceName));
    /// </code>
    /// </example>
    public const string ActivitySourceName = "TEC.Cqrs";

    /// <summary>
    /// Nome do <see cref="System.Diagnostics.Metrics.Meter"/> do pipeline, com os instrumentos
    /// <see cref="RequestsMetricName"/> e <see cref="NotificationsMetricName"/> (contadores) e
    /// <see cref="RequestDurationMetricName"/> e <see cref="NotificationDurationMetricName"/> (histogramas, em segundos).
    /// Exportado sem configuração pelo <c>AddEnterpriseObservability</c> do TEC.Observability (prefixo <c>TEC.*</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// // Sem o TEC.Observability:
    /// builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(CqrsDiagnostics.MeterName));
    /// </code>
    /// </example>
    public const string MeterName = "TEC.Cqrs";

    /// <summary>
    /// Contador <c>tec.cqrs.requests</c> (unidade <c>{request}</c>): requisições processadas, com os atributos
    /// <see cref="RequestTag"/>, <see cref="KindTag"/>, <see cref="OutcomeTag"/> e, em falha ou exceção, <see cref="ErrorTypeTag"/>.
    /// </summary>
    public const string RequestsMetricName = "tec.cqrs.requests";

    /// <summary>Histograma <c>tec.cqrs.request.duration</c> (unidade <c>s</c>): duração das requisições, com os mesmos atributos do contador.</summary>
    public const string RequestDurationMetricName = "tec.cqrs.request.duration";

    /// <summary>
    /// Contador <c>tec.cqrs.notifications</c> (unidade <c>{notification}</c>): publicações de notificação (todos os handlers),
    /// com os atributos <see cref="NotificationTag"/>, <see cref="OutcomeTag"/> e, em exceção, <see cref="ErrorTypeTag"/>.
    /// </summary>
    public const string NotificationsMetricName = "tec.cqrs.notifications";

    /// <summary>Histograma <c>tec.cqrs.notification.duration</c> (unidade <c>s</c>): duração das publicações, com os mesmos atributos do contador.</summary>
    public const string NotificationDurationMetricName = "tec.cqrs.notification.duration";

    /// <summary>Atributo com o nome completo do tipo da requisição.</summary>
    public const string RequestTag = "cqrs.request";

    /// <summary>Atributo com o nome completo do tipo da notificação.</summary>
    public const string NotificationTag = "cqrs.notification";

    /// <summary>Atributo com o tipo da requisição: <c>command</c>, <c>query</c> ou <c>request</c> (<c>notification</c> nas <c>Activity</c>s de publicação).</summary>
    public const string KindTag = "cqrs.kind";

    /// <summary>
    /// Atributo com o resultado: <c>success</c>, <c>failure</c> (<c>Result</c> de falha), <c>exception</c> ou <c>canceled</c>.
    /// Nas notificações: <c>success</c>, <c>exception</c> (algum handler lançou) ou <c>canceled</c>.
    /// </summary>
    public const string OutcomeTag = "cqrs.outcome";

    /// <summary>
    /// Atributo <c>error.type</c> (convenção do OpenTelemetry): o <c>ErrorType</c> da falha (ex.: <c>Validation</c>,
    /// <c>NotFound</c>; o interno, se houver) ou o nome completo do tipo da exceção.
    /// </summary>
    public const string ErrorTypeTag = "error.type";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// Indica se a exceção já foi registrada em log pelo pipeline (exceção não tratada em um handler). Use em tratamentos
    /// globais de erro próprios para não registrar a mesma exceção duas vezes (o <c>UseTecExceptionHandler</c> já faz isso).
    /// </summary>
    /// <remarks>
    /// Só é <c>true</c> se o log foi de fato escrito: com o nível <c>Error</c> desabilitado para a categoria do pipeline
    /// (<c>TEC.Cqrs.Internal.Mediator</c>), a exceção não é marcada.
    /// </remarks>
    public static bool IsExceptionLogged(Exception? exception) => LoggedExceptions.IsLogged(exception);

    /// <summary>
    /// Retorna as requisições (commands/queries) dos assemblies informados que não possuem handler registrado.
    /// Use em um teste de arquitetura para detectar handlers esquecidos antes de chegar em produção.
    /// </summary>
    /// <example>
    /// <code>
    /// [Fact]
    /// public void Todas_as_requisicoes_possuem_handler()
    /// {
    ///     var services = new ServiceCollection();
    ///     services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining&lt;Program&gt;());
    ///
    ///     Assert.Empty(CqrsDiagnostics.FindRequestsWithoutHandler(services, typeof(Program).Assembly));
    /// }
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    public static IReadOnlyList<Type> FindRequestsWithoutHandler(IServiceCollection services, params IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        var handled = RegisteredServices.GetGenericArguments(services, typeof(IRequestHandler<,>));
        return FindRequests(assemblies, type => !handled.Contains(type));
    }

    /// <summary>
    /// Retorna os commands dos assemblies informados sem validador registrado (<see cref="IRequestValidator{TRequest}"/>,
    /// inclusive os validators do FluentValidation) e sem <c>[SkipValidation]</c> (os mesmos que falhariam em execução com
    /// <c>CqrsOptions.RequireValidatorForCommands</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// Assert.Empty(CqrsDiagnostics.FindCommandsWithoutValidator(services, typeof(Program).Assembly));
    /// </code>
    /// </example>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    public static IReadOnlyList<Type> FindCommandsWithoutValidator(IServiceCollection services, params IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        var validated = RegisteredServices.GetGenericArguments(services, typeof(IRequestValidator<>));
        return FindRequests(assemblies, type => RequestMetadata.RequiresValidator(type) && !validated.Contains(type));
    }

    /// <summary>
    /// Retorna as requisições dos assemblies informados que não declaram autorização: sem <c>[AuthorizeRequest]</c>,
    /// sem <c>IRequestAuthorizer</c> registrado (do próprio tipo, de um tipo base ou de uma interface) e sem
    /// <c>[AllowAnonymousRequest]</c>. São as mesmas que, com <c>CqrsOptions.RequireAuthorization</c> (padrão), fazem o
    /// <c>AddTecCqrs</c> falhar. Útil para listar as pendências de assemblies com a exigência desativada.
    /// </summary>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    public static IReadOnlyList<Type> FindRequestsWithoutAuthorization(IServiceCollection services, params IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        var authorizerTargets = RegisteredServices.GetGenericArguments(services, typeof(IRequestAuthorizer<>));
        return FindRequests(assemblies, type => !RequestMetadata.DeclaresAuthorization(type, authorizerTargets));
    }

    /// <summary>Requisições concretas dos assemblies que atendem a <paramref name="predicate"/>, em ordem alfabética.</summary>
    [RequiresUnreferencedCode(TypeScanner.ScanningMessage)]
    private static Type[] FindRequests(IEnumerable<Assembly> assemblies, Func<Type, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        return [.. assemblies.Distinct()
            .SelectMany(TypeScanner.GetConcreteTypes)
            .Where(type => typeof(IBaseRequest).IsAssignableFrom(type) && predicate(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }
}
