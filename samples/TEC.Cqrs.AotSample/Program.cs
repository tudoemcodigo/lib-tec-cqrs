using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AotSample;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Persistence;

// Verificação de Native AOT do TEC.Cqrs. No CI, roda o binário publicado com PublishAot: sai com código 1 se alguma
// verificação falhar. Só registro explícito (a varredura de assemblies não é compatível com AOT).
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<OrderStore>();
services.AddScoped<IUnitOfWork, RecordingUnitOfWork>();
services
    .AddTecCqrs(options => options
        .AddCommandHandler<CreateOrderCommand, Guid, CreateOrderHandler>()
        .AddQueryHandler<GetOrderQuery, OrderDto, GetOrderHandler>()
        .AddRequestAuthorizer<ICustomerRequest, CustomerOwnerAuthorizer>()
        .AddRequestValidator<CreateOrderCommand, CreateOrderValidator>()
        .AddNotificationHandler<OrderCreatedEvent, OrderCreatedHandler>())
    .AddAspNetCore(http => http.JsonTypeInfoResolver = AotJsonContext.Default);

await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
var store = provider.GetRequiredService<OrderStore>();
int failures = 0;

// Verificações de subida (as mesmas do host com .AddAspNetCore())
foreach (var hosted in provider.GetServices<IHostedService>())
    await hosted.StartAsync(CancellationToken.None);

var created = await SendAsync("cliente-1", new CreateOrderCommand("cliente-1", 3));
Check(created.IsSuccess, "o dono cria o pedido (authorizer da interface, validador e handler)");
Check(store.Events.SequenceEqual(["begin", "handler", "commit", "notificacao"]), "transação confirmada e notificação depois do commit");

var otherCustomer = await SendAsync("cliente-2", new CreateOrderCommand("cliente-1", 1));
Check(otherCustomer.IsFailure && otherCustomer.Errors[0].Code == "PEDIDO_DE_OUTRO_CLIENTE", "outro cliente é negado pelo authorizer da interface");

var anonymous = await SendAsync(null, new CreateOrderCommand("cliente-1", 1));
Check(anonymous.IsFailure && anonymous.Errors[0].Type == ErrorType.Unauthorized, "anônimo é recusado antes dos authorizers");

var invalid = await SendAsync("cliente-1", new CreateOrderCommand("cliente-1", 0));
Check(invalid.IsFailure && invalid.Errors[0].Code == "QUANTIDADE_INVALIDA", "validador próprio recusa a quantidade inválida");

var (status, body) = await QueryAsJsonAsync("cliente-1", new GetOrderQuery("cliente-1", created.IsSuccess ? created.Value : Guid.Empty));
Check(status == StatusCodes.Status200OK && body.Contains("\"quantity\":3", StringComparison.Ordinal), "consulta serializada com o contexto JSON gerado");

Console.WriteLine(failures == 0 ? "Native AOT: todas as verificações passaram." : $"Native AOT: {failures} verificação(ões) falharam.");
return failures == 0 ? 0 : 1;

void Check(bool condition, string description)
{
    Console.WriteLine($"{(condition ? "ok   " : "FALHA")} {description}");
    if (!condition)
        failures++;
}

async Task<TResult> SendAsync<TResult>(string? user, IRequest<TResult> request)
    where TResult : Result
{
    await using var scope = provider.CreateAsyncScope();
    SetUser(scope.ServiceProvider, user);
    return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
}

async Task<(int Status, string Body)> QueryAsJsonAsync(string user, GetOrderQuery query)
{
    await using var scope = provider.CreateAsyncScope();
    var context = SetUser(scope.ServiceProvider, user);
    context.Response.Body = new MemoryStream();
    var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(query);
    await result.ToHttpResult().ExecuteAsync(context);
    context.Response.Body.Position = 0;
    using var reader = new StreamReader(context.Response.Body);
    return (context.Response.StatusCode, await reader.ReadToEndAsync());
}

static HttpContext SetUser(IServiceProvider services, string? user)
{
    var identity = user is null ? new ClaimsIdentity() : new ClaimsIdentity([new Claim("sub", user)], "Aot");
    var context = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity) };
    services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
    return context;
}
