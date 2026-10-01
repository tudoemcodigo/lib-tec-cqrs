using System.Collections.Concurrent;
using FluentValidation;
using Microsoft.Extensions.Logging;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Persistence;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Validação: dois validators para a mesma requisição -----

[AllowAnonymousRequest]
public sealed record DoisValidatorsCommand(string Nome, string Cpf) : ICommand;

internal sealed class DoisValidatorsHandler : ICommandHandler<DoisValidatorsCommand>
{
    public Task<Result> Handle(DoisValidatorsCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class DoisValidatorsNomeValidator : AbstractValidator<DoisValidatorsCommand>
{
    public DoisValidatorsNomeValidator() => RuleFor(c => c.Nome).NotEmpty().WithErrorCode("NOME_OBRIGATORIO");
}

internal sealed class DoisValidatorsCpfValidator : AbstractValidator<DoisValidatorsCommand>
{
    public DoisValidatorsCpfValidator() => RuleFor(c => c.Cpf).NotEmpty().WithErrorCode("CPF_OBRIGATORIO");
}

// ----- Autorização para tipo base / interface -----

/// <summary>Interface compartilhada por requisições de pedidos do cliente (autorizada por um único authorizer).</summary>
public interface IPedidoDoCliente : IBaseRequest
{
    Guid ClienteId { get; }
}

[SkipValidation]
public sealed record CancelarPedidoCommand(Guid ClienteId) : ICommand, IPedidoDoCliente;

internal sealed class CancelarPedidoHandler(CallLog log) : ICommandHandler<CancelarPedidoCommand>
{
    public Task<Result> Handle(CancelarPedidoCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class CancelarPedidoAuthorizer(CallLog log) : IRequestAuthorizer<CancelarPedidoCommand>
{
    public Task<Result> AuthorizeAsync(CancelarPedidoCommand request, CancellationToken cancellationToken)
    {
        log.Add("authorizer:concreto");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class PedidoDoClienteAuthorizer(CallLog log) : IRequestAuthorizer<IPedidoDoCliente>
{
    public Task<Result> AuthorizeAsync(IPedidoDoCliente request, CancellationToken cancellationToken)
    {
        log.Add("authorizer:interface");
        return Task.FromResult(request.ClienteId == Guid.Empty
            ? Result.Failure(Error.Forbidden("PEDIDO_DE_OUTRO_CLIENTE", "Acesso negado."))
            : Result.Success());
    }
}

/// <summary>Classe base autorizada por um authorizer próprio.</summary>
public abstract record ComandoAuditado : ICommand;

[SkipValidation]
public sealed record ExcluirContaCommand(bool Permitido) : ComandoAuditado;

internal sealed class ExcluirContaHandler(CallLog log) : ICommandHandler<ExcluirContaCommand>
{
    public Task<Result> Handle(ExcluirContaCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class ComandoAuditadoAuthorizer : IRequestAuthorizer<ComandoAuditado>
{
    public Task<Result> AuthorizeAsync(ComandoAuditado request, CancellationToken cancellationToken) =>
        Task.FromResult(request is ExcluirContaCommand { Permitido: true }
            ? Result.Success()
            : Result.Failure(Error.Forbidden("NAO_AUDITADO", "Acesso negado.")));
}

/// <summary>Sem autorização declarada (genérica aberta: fora da varredura; fechada só nos testes).</summary>
public sealed record SemAutorizacaoQuery<T> : IQuery<int>;

internal sealed class SemAutorizacaoHandler<T> : IQueryHandler<SemAutorizacaoQuery<T>, int>
{
    public Task<Result<int>> Handle(SemAutorizacaoQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

// ----- Notificações para tipo base / interface -----

public interface IEventoDeCliente : INotification;

public sealed record ClienteAtualizadoEvent(Guid Id) : IEventoDeCliente;

internal sealed class ClienteAtualizadoHandler(CallLog log) : INotificationHandler<ClienteAtualizadoEvent>
{
    public Task Handle(ClienteAtualizadoEvent notification, CancellationToken cancellationToken)
    {
        log.Add("atualizado");
        return Task.CompletedTask;
    }
}

internal sealed class AuditoriaDeClienteHandler(CallLog log) : INotificationHandler<IEventoDeCliente>
{
    public Task Handle(IEventoDeCliente notification, CancellationToken cancellationToken)
    {
        log.Add("auditoria");
        return Task.CompletedTask;
    }
}

/// <summary>Trata o evento concreto e a interface: deve rodar uma única vez (pelo tipo mais específico).</summary>
internal sealed class HandlerDuplo(CallLog log) : INotificationHandler<ClienteAtualizadoEvent>, INotificationHandler<IEventoDeCliente>
{
    public Task Handle(ClienteAtualizadoEvent notification, CancellationToken cancellationToken)
    {
        log.Add("duplo:concreto");
        return Task.CompletedTask;
    }

    public Task Handle(IEventoDeCliente notification, CancellationToken cancellationToken)
    {
        log.Add("duplo:interface");
        return Task.CompletedTask;
    }
}

/// <summary>Evento cujo handler concreto não pode ser criado (registrado com factory que lança, nos testes).</summary>
public sealed record EventoComHandlerQuebrado : IEventoDeCliente;

[AllowAnonymousRequest, SkipValidation]
public sealed record PublicarEventoQuebradoCommand : ICommand;

internal sealed class PublicarEventoQuebradoHandler(IPublisher publisher) : ICommandHandler<PublicarEventoQuebradoCommand>
{
    public Task<Result> Handle(PublicarEventoQuebradoCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new EventoComHandlerQuebrado());
        return Task.FromResult(Result.Success());
    }
}

// ----- Transação: falha de command aninhado -----

public enum ModoFalhaDoFilho
{
    Falha,
    Excecao,
    Validacao,
    QueryNaoEncontrada,
    SemTransacaoFalha,
    SemTransacaoExcecao
}

/// <summary>Command com <c>[SkipTransaction]</c> que falha (enviado de dentro de um command com transação).</summary>
[SkipTransaction, SkipValidation]
[AllowAnonymousRequest]
public sealed record SemTransacaoQueFalhaCommand(bool Lancar = false) : ICommand;

internal sealed class SemTransacaoQueFalhaHandler : ICommandHandler<SemTransacaoQueFalhaCommand>
{
    public Task<Result> Handle(SemTransacaoQueFalhaCommand command, CancellationToken cancellationToken) =>
        command.Lancar
            ? throw new InvalidOperationException("falha no command sem transação")
            : Task.FromResult(Result.Failure(Error.BusinessRule("FALHOU_SEM_TRANSACAO", "Falhou.")));
}

/// <summary>Handler que retorna null (bug do handler): a transação não pode ficar aberta.</summary>
[AllowAnonymousRequest, SkipValidation]
public sealed record RetornaNullCommand : ICommand;

internal sealed class RetornaNullHandler : ICommandHandler<RetornaNullCommand>
{
    public Task<Result> Handle(RetornaNullCommand command, CancellationToken cancellationToken) => Task.FromResult<Result>(null!);
}

/// <summary>Command que envia um command/query interno que falha e ignora a falha (retorna sucesso).</summary>
[AllowAnonymousRequest, SkipValidation]
public sealed record PaiIgnoraFalhaDoFilhoCommand(ModoFalhaDoFilho Modo) : ICommand;

internal sealed class PaiIgnoraFalhaDoFilhoHandler(ISender sender, IPublisher publisher, CallLog log)
    : ICommandHandler<PaiIgnoraFalhaDoFilhoCommand>
{
    public async Task<Result> Handle(PaiIgnoraFalhaDoFilhoCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new ClienteCriadoEvent(Guid.NewGuid()));

        switch (command.Modo)
        {
            case ModoFalhaDoFilho.Falha:
                await sender.Send(new CriarComEventoCommand(Falhar: true), cancellationToken);
                break;
            case ModoFalhaDoFilho.Excecao:
                try
                {
                    await sender.Send(new CriarComEventoCommand(Lancar: true), cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // O handler externo "trata" a exceção do interno
                }
                break;
            case ModoFalhaDoFilho.Validacao:
                await sender.Send(new CriarClienteCommand("", ""), cancellationToken);
                break;
            case ModoFalhaDoFilho.QueryNaoEncontrada:
                await sender.Send(new ObterClienteQuery(Guid.Empty), cancellationToken);
                break;
            case ModoFalhaDoFilho.SemTransacaoFalha:
                await sender.Send(new SemTransacaoQueFalhaCommand(), cancellationToken);
                break;
            case ModoFalhaDoFilho.SemTransacaoExcecao:
                try
                {
                    await sender.Send(new SemTransacaoQueFalhaCommand(Lancar: true), cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // O handler externo "trata" a exceção do interno
                }
                break;
        }

        log.Add("pai");
        return Result.Success();
    }
}

// ----- Concorrência no mesmo escopo -----

[AllowAnonymousRequest, SkipValidation]
public sealed record ParaleloCommand(string Nome, int AtrasoMs, bool Falhar = false) : ICommand;

internal sealed class ParaleloHandler(IPublisher publisher) : ICommandHandler<ParaleloCommand>
{
    public async Task<Result> Handle(ParaleloCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new ParaleloEvent(command.Nome));
        await Task.Delay(command.AtrasoMs, cancellationToken);
        return command.Falhar ? Result.Failure(Error.BusinessRule("FALHOU", "Falhou.")) : Result.Success();
    }
}

public sealed record ParaleloEvent(string Nome) : INotification;

internal sealed class ParaleloEventHandler(CallLog log) : INotificationHandler<ParaleloEvent>
{
    public Task Handle(ParaleloEvent notification, CancellationToken cancellationToken)
    {
        log.Add($"evento:{notification.Nome}");
        return Task.CompletedTask;
    }
}

/// <summary>Behavior que chama PublishAfterCommit antes do handler (fora do TransactionBehavior).</summary>
internal sealed class PublicarNoBehavior<TRequest, TResponse>(IPublisher publisher) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new ClienteCriadoEvent(Guid.NewGuid()));
        return next(cancellationToken);
    }
}

// ----- Logging / performance -----

[AllowAnonymousRequest, SkipValidation]
public sealed record PaiDeFalharCommand : ICommand;

internal sealed class PaiDeFalharHandler(ISender sender) : ICommandHandler<PaiDeFalharCommand>
{
    public Task<Result> Handle(PaiDeFalharCommand command, CancellationToken cancellationToken) =>
        sender.Send(new FalharCommand(), cancellationToken);
}

[AllowAnonymousRequest]
public sealed record CancelavelQuery : IQuery<int>;

internal sealed class CancelavelHandler : IQueryHandler<CancelavelQuery, int>
{
    public Task<Result<int>> Handle(CancelavelQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Result<int>>(1);
    }
}

[AllowAnonymousRequest, SkipValidation]
public sealed record LentoComExcecaoCommand : ICommand;

internal sealed class LentoComExcecaoHandler : ICommandHandler<LentoComExcecaoCommand>
{
    public async Task<Result> Handle(LentoComExcecaoCommand command, CancellationToken cancellationToken)
    {
        await Task.Delay(30, cancellationToken);
        throw new TimeoutException("timeout no banco");
    }
}

/// <summary>Logger que guarda os registros para inspeção nos testes.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    public sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class ListLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception), exception));
    }
}
