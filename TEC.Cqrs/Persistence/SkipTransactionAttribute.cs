namespace TEC.Cqrs.Persistence;

/// <summary>
/// Executa o command sem abrir transação (ex.: commands que só chamam serviços externos
/// ou que controlam a própria transação).
/// </summary>
/// <remarks>
/// Enviado de dentro de outro command com transação, continua sujeito à regra dos commands aninhados: se falhar
/// (<c>Result</c> de falha ou exceção), a transação do command externo é desfeita.
/// </remarks>
/// <example>
/// <code>
/// [SkipTransaction]
/// public sealed record EnviarEmailBoasVindasCommand(Guid ClienteId) : ICommand;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class SkipTransactionAttribute : Attribute
{
}
