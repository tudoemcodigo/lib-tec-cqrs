# Instruções para agentes — TEC.Cqrs

Este arquivo complementa as instruções herdadas de `AGENT_BASE.md`. O TEC.Cqrs implementa o padrão CQRS com um pipeline seguro por padrão (autorização, validação, transação, log e telemetria) que é aplicado a cada requisição em qualquer ponto de entrada (API, job ou mensageria).

## Escopo e segurança de alterações

- O escopo padrão é este repositório. Não altere outros repositórios `TEC.*`, não publique pacotes/tags/releases e não rode sincronizações que escrevam fora daqui sem pedido explícito.
- Antes de editar, confira `git status` e `git diff`. Preserve alterações não commitadas; não use reset, checkout destrutivo ou limpeza para descartar trabalho existente.
- Mudanças em APIs públicas do mediator, pipeline behaviors, contratos de autorização (`IRequestAuthorizer`, `IPermissionChecker`), validação (`IRequestValidator`), idempotência ou alvos .NET exigem revisão de compatibilidade, documentação, changelog e consumidores.
- Testes devem atravessar contratos públicos e verificar resultados observáveis. Para mudanças de comportamento, cubra entradas inválidas, limites, falhas e concorrência quando aplicável.
- Trate todo dado externo como não confiável. Não exponha segredos, PII ou conteúdo controlado pelo usuário em exceções, logs, URLs ou sumários.
- O pipeline falha fechado: autorização e validação obrigatórias são verificadas na subida; command sem `[AuthorizeRequest]` ou validador derruba o host.

## Mapa da implementação

- `TEC.Cqrs/`: biblioteca multi-target (`net8.0` e `net10.0`), núcleo do mediator e pipeline.
  - `Abstractions/`: `ICommand`, `IQuery`, `INotification`, `ISender`, `IPublisher`, `ICommandHandler`, `IQueryHandler`, `INotificationHandler`, `IRequestAuthorizer`, `IRequestValidator`, `IPermissionChecker`, `IUnitOfWork`, `IPrincipalAccessor`.
  - `Pipeline/`: behaviors ordenados (Logging, Exceções, Autorização, Validação, Behaviors próprios, Performance, Transação) + `PublishAfterCommit`.
  - `Authorization/`: `[AuthorizeRequest]`, `[AllowAnonymousRequest]`, `[RequirePermission]`, `TecPrincipalKinds`, `FallbackPolicy` fechada.
  - `Validation/`: validação obrigatória, `AbstractValidator` do FluentValidation, `[SkipValidation]`.
  - `Idempotency/`: `Idempotency-Key` com reserva contra requisições simultâneas (`SingleFlight`).
  - `Telemetry/`: `ActivitySource` e `Meter` `TEC.Cqrs` da BCL; assinados pelo TEC.Observability via prefixo `TEC.*`.
  - `DependencyInjection/`: `AddTecCqrs`, registro explícito (`AddCommandHandler`, `AddQueryHandler`, `AddNotificationHandler`) para AOT.
- `TEC.Cqrs.AspNetCore/`: integração ASP.NET Core (Minimal APIs, Controllers, OpenAPI), `ToHttpResult`, `UseTecExceptionHandler`, idempotência HTTP.
- `TEC.Cqrs.FluentValidation/`: executa `AbstractValidator<T>` no pipeline, um erro por campo.
- `TEC.Cqrs.Tests/`: testes de pipeline, regressão, segurança, robustez, permissões, idempotência, HTTP e divisão dos pacotes (TUnit + FsCheck).
- `TEC.Cqrs.LoadTests/`: carga rápida (`Carga-CI`) e suítes pesadas (`Carga-Pesada`: vazão, volume, soak, carga sustentada).
- `TEC.Cqrs.Benchmarks/`: medições BenchmarkDotNet.
- `samples/`: API de pedidos de exemplo e gerador de carga HTTP.
- `build/`: props/targets compartilhados de compilação.
- `docs/adr/`: decisões arquiteturais alinhadas ao livro *CQRS na Prática* (ADR-0001 a ADR-0010).
- `docs/`: guias por pipeline, autorização, validação, transação, idempotência, ASP.NET Core, AOT, segurança, testes, desenvolvimento.

## Contratos relevantes

- `Directory.Build.props` é a fonte única de versão do pacote (uma versão para os três pacotes). A versão inicial é `0.0.1`; não introduza versões nos `.csproj`.
- `Directory.Packages.props` pode declarar versões distintas por TFM para `Microsoft.Extensions.*`; condições por TFM são intencionais.
- Dependências TEC.* são declaradas com `<TecReference Include="..." />`, não com `PackageReference` direto.
- `Result`/`Error` do TEC.Core é o contrato de retorno; exceções de fluxo esperado não cruzam o pipeline (são convertidas em `Result`).
- `IUnitOfWork` do TEC.ORM é consumido pelo pipeline para transação por command; command interno que falha desfaz o externo inteiro.
- `ICurrentUser` do TEC.Core chega ao pipeline via `IPrincipalAccessor`; integração com TEC.Security é por abstração, não dependência direta.
- Workflows dos consumidores chamam `tudoemcodigo/tec-workflows`; confira as tags realmente publicadas antes de alterar referências.
- A licença é Apache-2.0. Mantenha `LICENSE`, `NOTICE`, badge/README e `<PackageLicenseExpression>` coerentes.

## Documentação

- `README.md` e `CHANGELOG.md` têm conteúdo próprio do TEC.Cqrs: siga o padrão do template, mantenha a marca Tudo em Código em todos os READMEs e não substitua o conteúdo específico por placeholders genéricos.
- Atualize `README.md`, `CHANGELOG.md` e os guias em `docs/` quando uma mudança alterar comportamento ou contrato.
- Registre decisões transversais em um ADR numerado e mantenha `docs/adr/README.md` e `docs/README.md` atualizados.
- Consulte `docs/revisao-tecnica.md` como relatório de auditoria com data e escopo; não trate resultados históricos como estado atual sem nova verificação.
- Workflows e configurações centrais do consumidor estão em `.github/workflows/`; mantenha `.github/workflows/README.md` fiel aos gatilhos, permissões e inputs reais.

## Verificação local

```powershell
dotnet restore TEC.Cqrs.slnx
dotnet build TEC.Cqrs.slnx -c Release
dotnet test --solution TEC.Cqrs.slnx -c Release
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"; dotnet test --solution TEC.Cqrs.slnx -c Release; Remove-Item Env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
git diff --check
```

O CI também valida convenções, pacote e workflows reutilizáveis. Não afirme que o CI está funcional apenas com build/testes locais: confirme que as tags chamadas existem e que os `permissions` do chamador incluem o mínimo exigido pelo workflow reutilizável.