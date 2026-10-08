using System.Runtime.CompilerServices;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Exceções já registradas em log pelo pipeline. Evita registrar a mesma exceção várias vezes: em cada nível de requisições
/// aninhadas e, depois, no <c>UseTecExceptionHandler</c>.
/// </summary>
/// <remarks>Tabela fraca: não mantém a exceção viva nem altera <see cref="Exception.Data"/>.</remarks>
internal static class LoggedExceptions
{
    private static readonly ConditionalWeakTable<Exception, object> Logged = [];

    /// <summary>Marca a exceção como registrada; <c>false</c> se ela já estava marcada.</summary>
    public static bool TryMark(Exception exception) => Logged.TryAdd(exception, Logged);

    public static bool IsLogged(Exception? exception) => exception is not null && Logged.TryGetValue(exception, out _);
}
