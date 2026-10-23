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

    [LoggerMessage(1023, LogLevel.Warning, "Requisição idempotente interrompida pelo cliente depois de uma resposta de sucesso: a reserva foi mantida até o LockTimeout (a operação pode ter sido confirmada).")]
    public static partial void AbortedAfterSuccess(ILogger logger);

    [LoggerMessage(1024, LogLevel.Warning, "Idempotency-Key ignorada: usuário autenticado sem nenhum dos UserIdClaimTypes (sem escopo). Revise IdempotencyOptions.UserIdClaimTypes.")]
    public static partial void UserWithoutIdClaim(ILogger logger);

    [LoggerMessage(1025, LogLevel.Error, "Requisição idempotente recusada (500): o endpoint exige Idempotency-Key, mas o usuário autenticado não tem nenhum dos UserIdClaimTypes. Revise IdempotencyOptions.UserIdClaimTypes.")]
    public static partial void RequiredKeyWithoutScope(ILogger logger);
}
