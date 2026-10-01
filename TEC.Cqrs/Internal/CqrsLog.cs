using Microsoft.Extensions.Logging;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Mensagens de log do pipeline (source generator: sem alocação quando o nível está desabilitado).
/// Segurança: o conteúdo das requisições nunca é registrado (pode conter dados pessoais); apenas nome, tipo e códigos de erro.
/// </summary>
internal static partial class CqrsLog
{
    [LoggerMessage(1000, LogLevel.Debug, "Processando {RequestKind} {RequestName}.")]
    public static partial void Handling(ILogger logger, string requestKind, string requestName);

    [LoggerMessage(1001, LogLevel.Information, "{RequestKind} {RequestName} concluído com sucesso em {ElapsedMilliseconds} ms.")]
    public static partial void Handled(ILogger logger, string requestKind, string requestName, long elapsedMilliseconds);

    [LoggerMessage(1002, LogLevel.Information, "{RequestKind} {RequestName} retornou falha {ErrorType} ({ErrorCodes}) em {ElapsedMilliseconds} ms.")]
    public static partial void HandledWithFailure(ILogger logger, string requestKind, string requestName, string errorType, string errorCodes, long elapsedMilliseconds);

    /// <remarks>
    /// Quando a falha veio de uma <c>AppException</c> convertida pelo pipeline, a exceção (com a inner exception) é anexada
    /// a este registro, que é o único da falha.
    /// </remarks>
    [LoggerMessage(1003, LogLevel.Error, "{RequestKind} {RequestName} retornou falha interna {ErrorType} ({ErrorCodes}) em {ElapsedMilliseconds} ms.")]
    public static partial void HandledWithInternalFailure(ILogger logger, Exception? exception, string requestKind, string requestName, string errorType, string errorCodes, long elapsedMilliseconds);

    [LoggerMessage(1004, LogLevel.Error, "{RequestKind} {RequestName} lançou exceção não tratada após {ElapsedMilliseconds} ms.")]
    public static partial void UnhandledException(ILogger logger, Exception exception, string requestKind, string requestName, long elapsedMilliseconds);

    [LoggerMessage(1005, LogLevel.Debug, "{RequestKind} {RequestName} cancelado após {ElapsedMilliseconds} ms.")]
    public static partial void Canceled(ILogger logger, string requestKind, string requestName, long elapsedMilliseconds);

    [LoggerMessage(1006, LogLevel.Warning, "{RequestKind} {RequestName} lento: {ElapsedMilliseconds} ms (limite: {ThresholdMilliseconds} ms).")]
    public static partial void SlowRequest(ILogger logger, string requestKind, string requestName, long elapsedMilliseconds, long thresholdMilliseconds);

    [LoggerMessage(1007, LogLevel.Error, "Falha ao desfazer a transação de {RequestName}.")]
    public static partial void RollbackFailed(ILogger logger, Exception exception, string requestName);

    [LoggerMessage(1008, LogLevel.Error, "Handler {HandlerName} falhou ao processar a notificação {NotificationName} publicada após o commit (a transação já foi confirmada).")]
    public static partial void DeferredNotificationFailed(ILogger logger, Exception exception, string notificationName, string handlerName);

    [LoggerMessage(1009, LogLevel.Error, "Falha ao criar os handlers ({ServiceName}) da notificação {NotificationName} publicada após o commit (a transação já foi confirmada).")]
    public static partial void DeferredNotificationResolutionFailed(ILogger logger, Exception exception, string notificationName, string serviceName);

    [LoggerMessage(1010, LogLevel.Warning, "Transação de {RequestName} desfeita: um command interno falhou.")]
    public static partial void NestedCommandRollback(ILogger logger, string requestName);

    // 1011 (Location descartado) é registrado pelo pacote TEC.Cqrs.AspNetCore
}
