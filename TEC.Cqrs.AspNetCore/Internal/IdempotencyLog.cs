using Microsoft.Extensions.Logging;

namespace TEC.Cqrs.AspNetCore.Internal;

/// <summary>Mensagens de log da idempotência HTTP (continuação da numeração do <c>TEC.Cqrs</c>). Nunca registra a chave nem o corpo.</summary>
internal static partial class IdempotencyLog
{
    [LoggerMessage(1017, LogLevel.Debug, "Requisição idempotente repetida: resposta guardada devolvida sem executar de novo.")]
    public static partial void Replayed(ILogger logger);

    [LoggerMessage(1018, LogLevel.Information, "Requisição idempotente recusada (409): outra com a mesma chave está em processamento.")]
    public static partial void InProgress(ILogger logger);

    [LoggerMessage(1019, LogLevel.Warning, "Requisição idempotente recusada (422): a chave já foi usada com outro conteúdo.")]
    public static partial void Mismatch(ILogger logger);

    [LoggerMessage(1020, LogLevel.Warning, "Resposta idempotente não guardada: excede {MaxBytes} bytes (MaxResponseBodyBytes). A chave foi liberada.")]
    public static partial void ResponseTooLarge(ILogger logger, int maxBytes);

    [LoggerMessage(1021, LogLevel.Warning, "Resposta idempotente não guardada: a reserva expirou (LockTimeout) e foi assumida por outra requisição.")]
    public static partial void LockLost(ILogger logger);

    [LoggerMessage(1022, LogLevel.Debug, "Idempotency-Key ignorada: requisição sem usuário identificado (sem escopo seguro).")]
    public static partial void AnonymousRequest(ILogger logger);
}
