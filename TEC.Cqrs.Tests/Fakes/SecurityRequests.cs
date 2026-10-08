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
public sealed record AuthenticatedQuery : IQuery<int>;

internal sealed class AuthenticatedHandler : IQueryHandler<AuthenticatedQuery, int>
{
    public Task<Result<int>> Handle(AuthenticatedQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Command com papel exigido e validator (para verificar que a autorização vem antes da validação).</summary>
[AuthorizeRequest(Roles = "Admin, Financeiro")]
public sealed record RefundCommand(string Reason) : ICommand;

internal sealed class RefundHandler(CallLog log) : ICommandHandler<RefundCommand>
{
    public Task<Result> Handle(RefundCommand command, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult(Result.Success());
    }
}

internal sealed class RefundValidator : AbstractValidator<RefundCommand>
{
    public RefundValidator() => RuleFor(c => c.Reason).NotEmpty().WithErrorCode("MOTIVO_OBRIGATORIO");
}

/// <summary>Policy que recebe a própria requisição como recurso.</summary>
[AuthorizeRequest(Policy = PolicyQuery.PolicyName)]
public sealed record PolicyQuery(string Owner) : IQuery<int>
{
    public const string PolicyName = "SomenteDono";
}

internal sealed class PolicyHandler : IQueryHandler<PolicyQuery, int>
{
    public Task<Result<int>> Handle(PolicyQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Autorização baseada no recurso via <see cref="IRequestAuthorizer{TRequest}"/>.</summary>
public sealed record GetOrderQuery(Guid OrderId) : IQuery<int>;

internal sealed class GetOrderHandler(CallLog log) : IQueryHandler<GetOrderQuery, int>
{
    public Task<Result<int>> Handle(GetOrderQuery query, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult<Result<int>>(1);
    }
}

internal sealed class GetOrderAuthorizer : IRequestAuthorizer<GetOrderQuery>
{
    public Task<Result> AuthorizeAsync(GetOrderQuery request, CancellationToken cancellationToken) =>
        Task.FromResult(request.OrderId == Guid.Empty
            ? Result.Failure(Error.NotFound("PEDIDO_NAO_ENCONTRADO", "Pedido não encontrado."))
            : Result.Success());
}

[AllowAnonymousRequest]
public sealed record PublishesQuery : IQuery<int>;

internal sealed class PublishesHandler : IQueryHandler<PublishesQuery, int>
{
    public Task<Result<int>> Handle(PublishesQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
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
public sealed record NoValidatorCommand : ICommand;

internal sealed class NoValidatorHandler : ICommandHandler<NoValidatorCommand>
{
    public Task<Result> Handle(NoValidatorCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

/// <summary>Handler que usa ValidateAndThrow (FluentValidation.ValidationException).</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record ValidateInHandlerCommand : ICommand;

internal sealed class ValidateInHandlerHandler : ICommandHandler<ValidateInHandlerCommand>
{
    public Task<Result> Handle(ValidateInHandlerCommand command, CancellationToken cancellationToken) =>
        throw new ValidationException([new ValidationFailure("Endereco.Cep", "CEP inválido.") { ErrorCode = "CEP_INVALIDO" }]);
}

// ----- Notificações após o commit -----

[SkipValidation]
[AllowAnonymousRequest]
public sealed record CreateWithEventCommand(bool Fail = false, bool Throw = false) : ICommand;

internal sealed class CreateWithEventHandler(IPublisher publisher, CallLog log) : ICommandHandler<CreateWithEventCommand>
{
    public Task<Result> Handle(CreateWithEventCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new CustomerCreatedEvent(Guid.NewGuid()));
        log.Add("handler");

        if (command.Throw)
            throw new InvalidOperationException("falha no handler");

        return Task.FromResult(command.Fail
            ? Result.Failure(Error.BusinessRule("FALHOU", "Falhou."))
            : Result.Success());
    }
}

/// <summary>Command que envia <see cref="CriarComEventoCommand"/> e ignora a falha do filho.</summary>
[SkipValidation]
[AllowAnonymousRequest]
public sealed record ParentWithEventCommand(bool ChildFails) : ICommand;

internal sealed class ParentWithEventHandler(ISender sender, CallLog log) : ICommandHandler<ParentWithEventCommand>
{
    public async Task<Result> Handle(ParentWithEventCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(new CreateWithEventCommand(Fail: command.ChildFails), cancellationToken);
        log.Add("pai");
        return Result.Success();
    }
}

[SkipValidation]
[AllowAnonymousRequest]
public sealed record PublishFailingEventCommand : ICommand;

internal sealed class PublishFailingEventHandler(IPublisher publisher) : ICommandHandler<PublishFailingEventCommand>
{
    public Task<Result> Handle(PublishFailingEventCommand command, CancellationToken cancellationToken)
    {
        publisher.PublishAfterCommit(new FailingEvent());
        return Task.FromResult(Result.Success());
    }
}

public sealed record FailingEvent : INotification;

internal sealed class FailingHandler : INotificationHandler<FailingEvent>
{
    public Task Handle(FailingEvent notification, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("SMTP fora do ar");
}

internal sealed class WorkingHandler(CallLog log) : INotificationHandler<FailingEvent>
{
    public Task Handle(FailingEvent notification, CancellationToken cancellationToken)
    {
        log.Add("segundo");
        return Task.CompletedTask;
    }
}

// ----- Requisições inválidas (genéricas abertas: ignoradas na varredura; fechadas apenas nos testes) -----

public sealed record CommandAndQuery<T> : ICommand, IQueryBase;

internal sealed class CommandAndQueryHandler<T> : ICommandHandler<CommandAndQuery<T>>
{
    public Task<Result> Handle(CommandAndQuery<T> command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

public sealed record NoMarkerRequest<T> : IRequest<Result>;

internal sealed class NoMarkerHandler<T> : IRequestHandler<NoMarkerRequest<T>, Result>
{
    public Task<Result> Handle(NoMarkerRequest<T> request, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

/// <summary>Interface compartilhada, validada por um validator próprio (que só vale via Include).</summary>
public interface IHasDocument
{
    string Document { get; }
}

/// <summary>A mesma interface, como requisição (para o registro explícito de validator de interface).</summary>
public interface IHasDocumentRequest : IHasDocument, IBaseRequest;

public sealed record WithDocumentCommand<T>(string Document) : ICommand, IHasDocumentRequest;

internal sealed class WithDocumentHandler<T> : ICommandHandler<WithDocumentCommand<T>>
{
    public Task<Result> Handle(WithDocumentCommand<T> command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class DocumentValidator<T> : AbstractValidator<IHasDocument>
{
    public DocumentValidator() => RuleFor(d => d.Document).NotEmpty();
}

internal sealed class DocumentRequestValidator<T> : AbstractValidator<IHasDocumentRequest>
{
    public DocumentRequestValidator() => RuleFor(d => d.Document).NotEmpty();
}

internal sealed class AllowAllAuthorizer<TRequest> : IRequestAuthorizer<TRequest>
    where TRequest : IBaseRequest
{
    public Task<Result> AuthorizeAsync(TRequest request, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class WithDocumentValidator<T> : AbstractValidator<WithDocumentCommand<T>>
{
    public WithDocumentValidator() => Include(new DocumentValidator<T>());
}

[AllowAnonymousRequest, AuthorizeRequest]
public sealed record AnonymousAndAuthorizedQuery<T> : IQuery<int>;

internal sealed class AnonymousAndAuthorizedHandler<T> : IQueryHandler<AnonymousAndAuthorizedQuery<T>, int>
{
    public Task<Result<int>> Handle(AnonymousAndAuthorizedQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Roles informado sem nenhum papel: não pode degradar para "qualquer usuário autenticado".</summary>
[AuthorizeRequest(Roles = " , ")]
public sealed record BlankRolesQuery<T> : IQuery<int>;

internal sealed class BlankRolesHandler<T> : IQueryHandler<BlankRolesQuery<T>, int>
{
    public Task<Result<int>> Handle(BlankRolesQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

[AuthorizeRequest(Policy = " ")]
public sealed record BlankPolicyQuery<T> : IQuery<int>;

internal sealed class BlankPolicyHandler<T> : IQueryHandler<BlankPolicyQuery<T>, int>
{
    public Task<Result<int>> Handle(BlankPolicyQuery<T> query, CancellationToken cancellationToken) => Task.FromResult<Result<int>>(1);
}

/// <summary>Interface de requisição protegida por um authorizer de posse (que sempre nega, nos testes).</summary>
public interface ICustomerResource : IBaseRequest;

[SkipValidation]
public sealed record AccessResourceCommand<T> : ICommand, ICustomerResource;

internal sealed class AccessResourceHandler<T> : ICommandHandler<AccessResourceCommand<T>>
{
    public Task<Result> Handle(AccessResourceCommand<T> command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed class DenyAuthorizer<TRequest> : IRequestAuthorizer<TRequest>
    where TRequest : IBaseRequest
{
    public Task<Result> AuthorizeAsync(TRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(Error.Forbidden("RECURSO_DE_OUTRO_CLIENTE", "Acesso negado.")));
}

/// <summary>Validador próprio (sem FluentValidation) de interface: nunca seria executado pelo pipeline.</summary>
internal sealed class RequiredDocumentValidator<TRequest> : IRequestValidator<TRequest>
    where TRequest : IHasDocumentRequest
{
    public Task<Result> ValidateAsync(TRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(request.Document)
            ? Result.Failure(Error.Validation("DOCUMENTO_OBRIGATORIO", "Documento é obrigatório.", "documento"))
            : Result.Success());
}
