using TEC.Core.Common.Results;

namespace TEC.Cqrs.Abstractions;

/// <summary>
/// Marcador de qualquer requisição enviada pelo mediator. Não implemente diretamente:
/// use <see cref="ICommand"/>, <see cref="ICommand{TValue}"/> ou <see cref="IQuery{TValue}"/>.
/// </summary>
public interface IBaseRequest
{
}

/// <summary>
/// Requisição cuja resposta é sempre um <see cref="Result"/> (ou <see cref="Result{T}"/>).
/// Isso permite que o pipeline interrompa o fluxo com falha sem lançar exceções.
/// </summary>
/// <remarks>
/// Não implemente diretamente: use <see cref="ICommand"/>, <see cref="ICommand{TValue}"/> ou <see cref="IQuery{TValue}"/>.
/// Requisições sem esse marcador são rejeitadas na inicialização (escapariam da transação e da validação obrigatória).
/// </remarks>
/// <typeparam name="TResponse"><see cref="Result"/> ou <see cref="Result{T}"/>.</typeparam>
public interface IRequest<TResponse> : IBaseRequest
    where TResponse : Result
{
}

/// <summary>Marcador de commands (alteram estado). Apenas commands participam de transação.</summary>
public interface ICommandBase : IBaseRequest
{
}

/// <summary>Marcador de queries (somente leitura; nunca participam de transação).</summary>
public interface IQueryBase : IBaseRequest
{
}

/// <summary>Command sem valor de retorno.</summary>
/// <example>
/// <code>
/// public sealed record InativarClienteCommand(Guid Id) : ICommand;
/// </code>
/// </example>
public interface ICommand : IRequest<Result>, ICommandBase
{
}

/// <summary>Command que retorna um valor (ex.: o Id do registro criado).</summary>
/// <typeparam name="TValue">Tipo do valor retornado em caso de sucesso.</typeparam>
/// <example>
/// <code>
/// public sealed record CriarClienteCommand(string Nome, string Cpf) : ICommand&lt;Guid&gt;;
/// </code>
/// </example>
public interface ICommand<TValue> : IRequest<Result<TValue>>, ICommandBase
{
}

/// <summary>Query (somente leitura) que retorna um valor.</summary>
/// <typeparam name="TValue">Tipo do valor retornado em caso de sucesso.</typeparam>
/// <example>
/// <code>
/// public sealed record ObterClienteQuery(Guid Id) : IQuery&lt;ClienteDto&gt;;
/// </code>
/// </example>
public interface IQuery<TValue> : IRequest<Result<TValue>>, IQueryBase
{
}
