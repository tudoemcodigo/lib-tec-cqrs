using System.Diagnostics.CodeAnalysis;
using TEC.Core.Common.Results;

namespace TEC.Cqrs.Abstractions;

/// <summary>
/// Converte exceções conhecidas de outras bibliotecas em erros do <see cref="Result"/> (ex.: a
/// <c>FluentValidation.ValidationException</c>, registrada pelo pacote <c>TEC.Cqrs.FluentValidation</c>).
/// </summary>
/// <remarks>
/// <para>Usado pelo behavior de exceções do pipeline (a exceção lançada no handler vira <c>Result</c> de falha) e pelo
/// <c>UseTecExceptionHandler</c> do <c>TEC.Cqrs.AspNetCore</c> (a exceção vira a resposta HTTP correspondente).</para>
/// <para>Registre como <c>Singleton</c> (<c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IExceptionErrorMapper, MeuMapper&gt;())</c>).
/// O primeiro mapper que reconhecer a exceção decide os erros. As <c>AppException</c> do TEC.Core já são convertidas
/// pelo pipeline e não precisam de mapper.</para>
/// <para>Os erros devem ser seguros para o cliente: não inclua mensagens internas da exceção.</para>
/// </remarks>
public interface IExceptionErrorMapper
{
    /// <summary>Converte a exceção; <c>false</c> se ela não for reconhecida.</summary>
    /// <param name="exception">Exceção lançada.</param>
    /// <param name="errors">Erros correspondentes (ao menos um) quando reconhecida.</param>
    bool TryMap(Exception exception, [NotNullWhen(true)] out IReadOnlyList<Error>? errors);
}
