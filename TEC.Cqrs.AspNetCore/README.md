<div align="center">

<img src="https://raw.githubusercontent.com/tudoemcodigo/lib-tec-cqrs/main/Images/Logo.png" alt="TEC.Cqrs" width="100" />

# 🌐 TEC.Cqrs.AspNetCore

**Liga o TEC.Cqrs às APIs ASP.NET Core: usuário e policies da requisição no pipeline, `Result` convertido na resposta HTTP padronizada e exceções tratadas sem expor detalhes.**

[📚 Documentação do pacote](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/aspnetcore.md) · [📚 TEC.Cqrs](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/README.md) · [🐙 Repositório](https://github.com/tudoemcodigo/lib-tec-cqrs)

</div>

## ✨ O que é

- **`.AddAspNetCore()`**: o usuário da autorização passa a ser o `HttpContext.User` e as policies de
  `[AuthorizeRequest(Policy = ...)]` são avaliadas pelo `IAuthorizationService`.
- **`ToHttpResult()` / `ToCreatedHttpResult(...)`**: `Result` → `ApiResponse`/`PagedResponse` do TEC.Core com o status do
  `ErrorType` (400, 401, 403, 404, 409, 422, 429, 500, 502), `traceId` nas falhas e metadados OpenAPI automáticos. Minimal
  APIs e Controllers.
- **`UseTecExceptionHandler()`**: exceções viram `ApiResponse`; 500/502 com mensagem genérica, sem log duplicado.
- `Location` só com caminho relativo seguro; JSON com metadados gerados em compilação para Native AOT.

## 🎯 Quando usar

APIs ASP.NET Core (Minimal APIs ou Controllers) que enviam commands e queries do TEC.Cqrs.

## 📥 Instalação

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.Cqrs.AspNetCore --version 0.0.1
```

> [!IMPORTANT]
> **Use a mesma versão do `TEC.Cqrs` (e do `TEC.Cqrs.FluentValidation`, se houver).** Este pacote usa tipos internos do
> núcleo (`InternalsVisibleTo`) e é publicado junto com ele a cada release; versões diferentes podem falhar em execução
> (`MissingMethodException`, `TypeLoadException`). O pacote já traz o `TEC.Cqrs` na versão certa.

## 🚀 Início rápido

```csharp
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.DependencyInjection;

builder.Services.AddTecCqrs(options => options.RegisterServicesFromAssemblyContaining<Program>())
    .AddAspNetCore();
builder.Services.AddAuthorization(o => o.AddPolicy("CustomersWrite", p => p.RequireRole("Admin")));

var app = builder.Build();
app.UseTecExceptionHandler();   // antes dos demais middlewares

app.MapPost("/clientes", (CreateCustomerCommand command, ISender sender, CancellationToken ct) =>
    sender.Send(command, ct).ToCreatedHttpResult(id => $"/clientes/{id}"));     // 201 + Location

app.MapGet("/clientes/{id:guid}", (Guid id, ISender sender, CancellationToken ct) =>
    sender.Send(new GetCustomerQuery(id), ct).ToHttpResult());                  // 200, 404...
```

## 📚 Documentação

Opções (`JsonTypeInfoResolver`, `ReplaceExistingPrincipalAccessor`), formato das respostas, paginação, OpenAPI e
tratamento de exceções: [docs/aspnetcore.md](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/aspnetcore.md).

`net8.0` e `net10.0` · Native AOT · [MIT](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
