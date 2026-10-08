using TEC.Core.Common.Results;

namespace TEC.Cqrs.Abstractions;

/// <summary>
/// Handler de uma requisição. Prefira as interfaces específicas: <see cref="ICommandHandler{TCommand}"/>,
/// <see cref="ICommandHandler{TCommand, TValue}"/> e <see cref="IQueryHandler{TQuery, TValue}"/>.
/// </summary>
/// <remarks>
/// Cada requisição deve ter exatamente um handler. Handlers são registrados (com tempo de vida <c>Scoped</c>) pelo
/// <c>AddTecCqrs</c>, por varredura de assemblies ou explicitamente (<c>AddCommandHandler</c>/<c>AddQueryHandler</c>,
/// compatível com Native AOT), e podem ser <c>internal</c>.
/// </remarks>
public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    /// <summary>Processa a requisição.</summary>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>Handler de um command sem valor de retorno.</summary>
public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Result>
    where TCommand : ICommand
{
}

/// <summary>Handler de um command com valor de retorno.</summary>
/// <example>
/// <code>
/// internal sealed class CriarClienteHandler(IClienteRepository repositorio)
///     : ICommandHandler&lt;CriarClienteCommand, Guid&gt;
/// {
///     public async Task&lt;Result&lt;Guid&gt;&gt; Handle(CriarClienteCommand command, CancellationToken cancellationToken)
///     {
///         if (await repositorio.ExisteCpfAsync(command.Cpf, cancellationToken))
///             return Error.Conflict("CLIENTE_DUPLICADO", "Já existe um cliente com este CPF.");
///
///         var cliente = new Cliente(command.Nome, command.Cpf);
///         await repositorio.AdicionarAsync(cliente, cancellationToken);
///         return cliente.Id;
///     }
/// }
/// </code>
/// </example>
public interface ICommandHandler<in TCommand, TValue> : IRequestHandler<TCommand, Result<TValue>>
    where TCommand : ICommand<TValue>
{
}

/// <summary>Handler de uma query.</summary>
public interface IQueryHandler<in TQuery, TValue> : IRequestHandler<TQuery, Result<TValue>>
    where TQuery : IQuery<TValue>
{
}
