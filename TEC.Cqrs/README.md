<div align="center">

<img src="https://raw.githubusercontent.com/tudoemcodigo/lib-tec-cqrs/main/Images/Logo.png" alt="TEC.Cqrs" width="100" />

# 🧭 TEC.Cqrs

**Commands, queries e notificações com um pipeline seguro por padrão: log, autorização, validação e transação aplicados a cada requisição, eventos publicados só depois do commit.**

[📚 Documentação](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/README.md) · [🚀 Início rápido](https://github.com/tudoemcodigo/lib-tec-cqrs#-início-rápido) · [🐙 Repositório](https://github.com/tudoemcodigo/lib-tec-cqrs)

</div>

## ✨ O que é

O núcleo do TEC.Cqrs: mediator próprio (`ISender`, `IPublisher`, `IMediator`), requisições `ICommand`/`ICommand<T>`/`IQuery<T>`
com handlers que retornam `Result`/`Result<T>` do TEC.Core, e um pipeline em ordem fixa:

**Logging → Exceções → Autorização → Validação → behaviors próprios → Performance → Transação → Handler**

- **Autorização obrigatória** (`[AuthorizeRequest]`, `IRequestAuthorizer<T>` ou `[AllowAnonymousRequest]`), verificada na subida.
- **Validação obrigatória** de commands (`IRequestValidator<T>` ou `[SkipValidation]`), fail closed.
- **Transação por command** com o seu `IUnitOfWork`; command interno que falha desfaz o externo.
- **`PublishAfterCommit`**: eventos publicados só depois do commit, descartados em falha.
- **Telemetria** pelo `ActivitySource` e `Meter` `TEC.Cqrs`, sem o conteúdo das requisições.
- Sem ASP.NET Core e sem FluentValidation: serve a APIs, workers, jobs e mensageria. Compatível com Native AOT.

## 🎯 Quando usar

- Em qualquer aplicação com o TEC.Cqrs: os pacotes `TEC.Cqrs.AspNetCore` e `TEC.Cqrs.FluentValidation` já o trazem.
- Sozinho em workers, `BackgroundService`, consumidores de fila e jobs (registre um `IPrincipalAccessor` com a identidade do processo).

## 📥 Instalação

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.Cqrs --version 0.0.1
```

> [!IMPORTANT]
> Se usar também `TEC.Cqrs.AspNetCore` ou `TEC.Cqrs.FluentValidation`, mantenha **todos os `TEC.Cqrs.*` na mesma versão**:
> os satélites usam tipos internos do núcleo e são publicados juntos com ele.

## 🚀 Início rápido

```csharp
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.Authorization;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Validation;

[AllowAnonymousRequest, SkipValidation]   // job interno, sem usuário e sem entrada do usuário
public sealed record CloseDayCommand(DateOnly Day) : ICommand<int>;

internal sealed class CloseDayHandler(IOrderRepository orders) : ICommandHandler<CloseDayCommand, int>
{
    public async Task<Result<int>> Handle(CloseDayCommand command, CancellationToken cancellationToken) =>
        await orders.CloseAsync(command.Day, cancellationToken);
}

// Registro compatível com Native AOT (ou options.RegisterServicesFromAssemblyContaining<Program>())
services.AddTecCqrs(options => options.AddCommandHandler<CloseDayCommand, int, CloseDayHandler>());

// Uso: sempre dentro de um escopo
await using var scope = scopeFactory.CreateAsyncScope();
var sender = scope.ServiceProvider.GetRequiredService<ISender>();
Result<int> closed = await sender.Send(new CloseDayCommand(DateOnly.FromDateTime(DateTime.UtcNow)), cancellationToken);
```

## 📚 Documentação

Commands e queries, mediator, pipeline, autorização, validação, notificações, transação, observabilidade, opções e Native
AOT: [docs/README.md](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/README.md).

`net8.0` e `net10.0` · Native AOT · [MIT](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
