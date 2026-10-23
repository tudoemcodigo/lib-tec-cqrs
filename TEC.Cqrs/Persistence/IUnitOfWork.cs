namespace TEC.Cqrs.Persistence;

/// <summary>
/// Unidade de trabalho usada pelo pipeline para envolver cada command em uma transação.
/// Implemente na camada de infraestrutura (EF Core, Dapper etc.) e registre como <c>Scoped</c>.
/// Sem implementação registrada, os commands são executados sem transação.
/// </summary>
/// <remarks>
/// Fluxo do pipeline para cada command: <see cref="BeginTransactionAsync"/> → handler →
/// <see cref="CommitAsync"/> (se o <c>Result</c> for sucesso) ou <see cref="RollbackAsync"/> (falha ou exceção).
/// Commands enviados de dentro de outro command reaproveitam a transação já aberta
/// (<see cref="HasActiveTransaction"/>). Se um deles falhar (<c>Result</c> de falha ou exceção), o command externo faz
/// <see cref="RollbackAsync"/> de toda a transação e retorna falha, mesmo que o handler externo tenha tratado o erro.
/// O pipeline usa uma única transação por escopo: commands com transação enviados em paralelo no mesmo escopo
/// (<c>Task.WhenAll</c>) são rejeitados com <see cref="InvalidOperationException"/>.
/// </remarks>
/// <example>
/// Implementação com EF Core:
/// <code>
/// internal sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
/// {
///     public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;
///
///     public Task BeginTransactionAsync(CancellationToken cancellationToken) =>
///         db.Database.BeginTransactionAsync(cancellationToken);
///
///     public async Task CommitAsync(CancellationToken cancellationToken)
///     {
///         await db.SaveChangesAsync(cancellationToken);
///         await db.Database.CommitTransactionAsync(cancellationToken);
///     }
///
///     public Task RollbackAsync(CancellationToken cancellationToken) =>
///         db.Database.RollbackTransactionAsync(cancellationToken);
/// }
///
/// services.AddScoped&lt;IUnitOfWork, EfUnitOfWork&gt;();
/// </code>
/// </example>
public interface IUnitOfWork
{
    /// <summary>Indica se já existe uma transação aberta no escopo atual.</summary>
    bool HasActiveTransaction { get; }

    /// <summary>Abre a transação.</summary>
    Task BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Persiste as alterações pendentes e confirma a transação.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Desfaz a transação.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
