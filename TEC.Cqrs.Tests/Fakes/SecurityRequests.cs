using System.Security.Claims;
using FluentValidation;
using FluentValidation.Results;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.Validation;

namespace TEC.Cqrs.Tests.Fakes;

// ----- Autorização -----

[AuthorizeRequest]
public sealed record AutenticadoQuery : IQuery<int>;

internal sealed class AutenticadoHandler : IQueryHandler<AutenticadoQuery, int>
{
    public Task<Result<int>> Handle(AutenticadoQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Command com papel exigido e validator (para verificar que a autorização vem antes da validação).</summary>
[AuthorizeRequest(Roles = "Admin, Financeiro")]
public sealed record EstornarCommand(string Motivo) : ICommand;

internal sealed class EstornarHandler(CallLog log) : ICommandHandler<EstornarCommand>
{
    public Task<Result> Handle(EstornarCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class EstornarValidator : AbstractValidator<EstornarCommand>
{
    public EstornarValidator() => RuleFor(c => c.Motivo).NotEmpty().WithErrorCode("MOTIVO_OBRIGATORIO");
}

/// <summary>Policy que recebe a própria requisição como recurso.</summary>
[AuthorizeRequest(Policy = PolicyQuery.PolicyName)]
public sealed record PolicyQuery(string Dono) : IQuery<int>
{
    public const string PolicyName = "SomenteDono";
}

internal sealed class PolicyHandler : IQueryHandler<PolicyQuery, int>
{
    public Task<Result<int>> Handle(PolicyQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Autorização baseada no recurso via <see cref="IRequestAuthorizer{TRequest}"/>.</summary>
public sealed record ObterPedidoQuery(Guid PedidoId) : IQuery<int>;

internal sealed class ObterPedidoHandler(CallLog log) : IQueryHandler<ObterPedidoQuery, int>
{
    public Task<Result<int>> Handle(ObterPedidoQuery query, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult<Result<int>>(1);
    }
}

internal sealed class ObterPedidoAuthorizer : IRequestAuthorizer<ObterPedidoQuery>
{
    public Task<Result> AuthorizeAsync(ObterPedidoQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(request.PedidoId == Guid.Empty
            ? Result.Failure(Error.NotFound("PEDIDO_NAO_ENCONTRADO", "Pedido não encontrado."))
            : Result.Success());
}

[AllowAnonymousRequest]
public sealed record PublicaQuery : IQuery<int>;

internal sealed class PublicaHandler : IQueryHandler<PublicaQuery, int>
{
    public Task<Result<int>> Handle(PublicaQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Usuário configurável pelo teste.</summary>
public sealed class FakePrincipalAccessor : IPrincipalAccessor
{
    public ClaimsPrincipal? Principal { get; set; }

    public static ClaimsPrincipal User(string name, params string[] roles) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), .. roles.Select(r => new Claim(ClaimTypes.Role, r))], authenticationType: "Teste"));
}

// ----- Validação obrigatória -----

/// <summary>Command propositalmente sem validator e sem [SkipValidation].</summary>
[AllowAnonymousRequest]
public sealed record SemValidatorCommand : ICommand;

internal sealed class SemValidatorHandler : ICommandHandler<SemValidatorCommand>
{
    public Task<Result> Handle(SemValidatorCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

/// <summary>Handler que usa ValidateAndThrow (FluentValidation.ValidationException).</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record ValidarNoHandlerCommand : ICommand;

internal sealed class ValidarNoHandlerHandler : ICommandHandler<ValidarNoHandlerCommand>
{
    public Task<Result> Handle(ValidarNoHandlerCommand command, CancellationToken cancellationToken) =>
        throw new ValidationException([new ValidationFailure("Endereco.Cep", "CEP inválido.") { ErrorCode = "CEP_INVALIDO" }]);
}

// ----- Notificações após o commit -----

[SkipValidation]
[AllowAnonymousRequest]
public sealed record CriarComEventoCommand(bool Falhar = false, bool Lancar = false) : ICommand;

internal sealed class CriarComEventoHandler(IPublisher publisher, CallLog log) : ICommandHandler<CriarComEventoCommand>
{
    public Task<Result> Handle(CriarComEventoCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new ClienteCriadoEvent(Guid.NewGuid()));
        log.Add("handler");

        if (command.Lancar)
            throw new InvalidOperationException("falha no handler");

        return Task.FromResult(command.Falhar
            ? Result.Failure(Error.BusinessRule("FALHOU", "Falhou."))
            : Result.Success());
    }
}

/// <summary>Command que envia <see cref="CriarComEventoCommand"/> e ignora a falha do filho.</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record PaiComEventoCommand(bool FilhoFalha) : ICommand;

internal sealed class PaiComEventoHandler(ISender sender, CallLog log) : ICommandHandler<PaiComEventoCommand>
{
    public async Task<Result> Handle(PaiComEventoCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(new CriarComEventoCommand(Falhar: command.FilhoFalha), cancellationToken);
        log.Add("pai");
        return Result.Success();
    }
}

[SkipValidation]
[AllowAnonymousRequest]
public sealed record PublicarEventoComFalhaCommand : ICommand;

internal sealed class PublicarEventoComFalhaHandler(IPublisher publisher) : ICommandHandler<PublicarEventoComFalhaCommand>
{
    public Task<Result> Handle(PublicarEventoComFalhaCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new EventoComFalha());
        return Task.FromResult(Result.Success());
    }
}

public sealed record EventoComFalha : INotification;

internal sealed class HandlerQueFalha : INotificationHandler<EventoComFalha>
{
    public Task Handle(EventoComFalha notification, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("SMTP fora do ar");
}

internal sealed class HandlerQueFunciona(CallLog log) : INotificationHandler<EventoComFalha>
{
    public Task Handle(EventoComFalha notification, CancellationToken cancellationToken)
    {
        log.Add("segundo");
        return Task.CompletedTask;
    }
}

// ----- Requisições inválidas (genéricas abertas: ignoradas na varredura; fechadas apenas nos testes) -----

public sealed record CommandEQuery<T> : ICommand, IQueryBase;

internal sealed class CommandEQueryHandler<T> : ICommandHandler<CommandEQuery<T>>
{
    public Task<Result> Handle(CommandEQuery<T> command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

public sealed record SemMarcadorRequest<T> : IRequest<Result>;

internal sealed class SemMarcadorHandler<T> : IRequestHandler<SemMarcadorRequest<T>, Result>
{
    public Task<Result> Handle(SemMarcadorRequest<T> request, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

/// <summary>Interface compartilhada, validada por um validator próprio (que só vale via Include).</summary>
public interface IPossuiDocumento
{
    string Documento { get; }
}

/// <summary>A mesma interface, como requisição (para o registro explícito de validator de interface).</summary>
public interface IPossuiDocumentoRequest : IPossuiDocumento, IBaseRequest;

public sealed record ComDocumentoCommand<T>(string Documento) : ICommand, IPossuiDocumentoRequest;

internal sealed class ComDocumentoHandler<T> : ICommandHandler<ComDocumentoCommand<T>>
{
    public Task<Result> Handle(ComDocumentoCommand<T> command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class DocumentoValidator<T> : AbstractValidator<IPossuiDocumento>
{
    public DocumentoValidator() => RuleFor(d => d.Documento).NotEmpty();
}

internal sealed class DocumentoRequestValidator<T> : AbstractValidator<IPossuiDocumentoRequest>
{
    public DocumentoRequestValidator() => RuleFor(d => d.Documento).NotEmpty();
}

internal sealed class PermitirTudoAuthorizer<TRequest> : IRequestAuthorizer<TRequest>
    where TRequest : IBaseRequest
{
    public Task<Result> AuthorizeAsync(TRequest request, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class ComDocumentoValidator<T> : AbstractValidator<ComDocumentoCommand<T>>
{
    public ComDocumentoValidator() => Include(new DocumentoValidator<T>());
}

[AllowAnonymousRequest, AuthorizeRequest]
public sealed record AnonimoEAutorizadoQuery<T> : IQuery<int>;

internal sealed class AnonimoEAutorizadoHandler<T> : IQueryHandler<AnonimoEAutorizadoQuery<T>, int>
{
    public Task<Result<int>> Handle(AnonimoEAutorizadoQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Roles informado sem nenhum papel: não pode degradar para "qualquer usuário autenticado".</summary>
[AuthorizeRequest(Roles = " , ")]
public sealed record RolesEmBrancoQuery<T> : IQuery<int>;

internal sealed class RolesEmBrancoHandler<T> : IQueryHandler<RolesEmBrancoQuery<T>, int>
{
    public Task<Result<int>> Handle(RolesEmBrancoQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

[AuthorizeRequest(Policy = " ")]
public sealed record PolicyEmBrancoQuery<T> : IQuery<int>;

internal sealed class PolicyEmBrancoHandler<T> : IQueryHandler<PolicyEmBrancoQuery<T>, int>
{
    public Task<Result<int>> Handle(PolicyEmBrancoQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Interface de requisição protegida por um authorizer de posse (que sempre nega, nos testes).</summary>
public interface IRecursoDoCliente : IBaseRequest;

[SkipValidation]
public sealed record AcessarRecursoCommand<T> : ICommand, IRecursoDoCliente;

internal sealed class AcessarRecursoHandler<T> : ICommandHandler<AcessarRecursoCommand<T>>
{
    public Task<Result> Handle(AcessarRecursoCommand<T> command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class NegarAuthorizer<TRequest> : IRequestAuthorizer<TRequest>
    where TRequest : IBaseRequest
{
    public Task<Result> AuthorizeAsync(TRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(Error.Forbidden("RECURSO_DE_OUTRO_CLIENTE", "Acesso negado.")));
}

/// <summary>Validador próprio (sem FluentValidation) de interface: nunca seria executado pelo pipeline.</summary>
internal sealed class DocumentoObrigatorioValidator<TRequest> : IRequestValidator<TRequest>
    where TRequest : IPossuiDocumentoRequest
{
    public Task<Result> ValidateAsync(TRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(request.Documento)
            ? Result.Failure(Error.Validation("DOCUMENTO_OBRIGATORIO", "Documento é obrigatório.", "documento"))
            : Result.Success());
}
