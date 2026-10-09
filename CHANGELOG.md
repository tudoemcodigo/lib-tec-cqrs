# 📝 Changelog

Todas as mudanças relevantes do **TEC.Cqrs** são registradas aqui. O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/). Enquanto a versão for `0.x`, mudanças incompatíveis podem ocorrer em versões MINOR. Os três pacotes saem sempre juntos, com a mesma versão, e devem ser usados na mesma versão (os satélites usam internos do núcleo).

## [0.1.0] - não publicado

### ✨ Adicionado

#### 🧭 TEC.Cqrs

- **Permissões declarativas:** `[RequirePermission(params string[])]` com `Mode = PermissionMatch.All | Any` (cada atributo é uma regra; todas as regras precisam passar), verificado pelo `AuthorizationBehavior` depois de `[AuthorizeRequest]` e antes dos `IRequestAuthorizer<T>`. Implica autenticação (401 `NAO_AUTENTICADO`), nega com 403 `ACESSO_NEGADO` e conta como autorização declarada para `RequireAuthorization`. Regras pré-processadas por tipo (sem reflexão por chamada); atributo sem permissões, com permissão em branco, `Mode` inválido ou junto com `[AllowAnonymousRequest]` falha na inicialização.
- `IPermissionChecker` (assíncrono, substituível via DI) e `ClaimPermissionChecker` padrão, que lê os claims de `CqrsOptions.PermissionClaimType` (padrão `tec_perm`, o mesmo do TEC.Security).
- `CqrsDiagnostics.FindRequestsWithoutPermission` para testes de arquitetura.
- **Idempotência** (`TEC.Cqrs.Idempotency`): `IIdempotencyStore` com reserva (`TryBeginAsync` → `Started`/`Completed`/`InProgress`/`Mismatch`, `CompleteAsync`/`AbandonAsync` só com o `LockId` vigente, expiração de reservas órfãs) e `InMemoryIdempotencyStore` limitado (`AddInMemoryIdempotencyStore`).

#### 🌐 TEC.Cqrs.AspNetCore

- `.AddIdempotency()` + `app.UseTecIdempotency()` + `.WithIdempotency()`/`[Idempotent]`: `Idempotency-Key` por usuário, hash SHA-256 de método, caminho, query e corpo; repete a resposta 2xx (`Idempotent-Replayed: true`), 409 `IDEMPOTENCIA_EM_ANDAMENTO` para requisições simultâneas (a operação executa uma única vez), 422 `IDEMPOTENCY_KEY_REUTILIZADA`, 400 para chave ausente (quando obrigatória) ou inválida, 413 para corpo acima do limite; libera a chave em resposta de erro ou exceção. `IdempotencyOptions` validadas na inicialização; logs 1017–1022 sem chave nem corpo.

## [0.0.1] - 2026-10-08

Primeira versão publicada.

### ✨ Adicionado

#### 🧭 TEC.Cqrs (núcleo, sem ASP.NET Core nem FluentValidation)

- **Mediator próprio** (`ISender`, `IPublisher`, `IMediator`), `Scoped`, com o executor de cada requisição montado no registro e guardado num `FrozenDictionary` (sem reflexão por chamada).
- **Commands e queries** (`ICommand`, `ICommand<T>`, `IQuery<T>`) com handlers (`ICommandHandler<>`, `ICommandHandler<,>`, `IQueryHandler<,>`) que retornam `Result`/`Result<T>` do TEC.Core; requisição que implementa `IRequest<T>` direto ou é command e query ao mesmo tempo é rejeitada.
- **Pipeline em ordem fixa:** Logging → Exceções → Autorização → Validação → behaviors próprios (`AddBehavior`) → Performance → Transação → Handler; behaviors usados sem cópia por `Send`.
- **Registro** por varredura de assemblies (`RegisterServicesFromAssembly`) ou explícito e compatível com Native AOT (`AddCommandHandler`, `AddQueryHandler`, `AddNotificationHandler`, `AddRequestAuthorizer`, `AddRequestValidator`); `AddTecCqrs` com `CqrsOptions` congeladas ao final e retorno `ICqrsBuilder` para os satélites.
- **Autorização no pipeline**, antes da validação: `[AuthorizeRequest]` (autenticação, `Roles`, `Policy`), `[AllowAnonymousRequest]` e `IRequestAuthorizer<T>` de tipo exato, base ou interface; identidade pelo `IPrincipalAccessor` e policies pelo `IRequestPolicyEvaluator`. Autorização obrigatória por padrão (`RequireAuthorization = true`), verificada na subida; fail closed (sem usuário → 401 `NAO_AUTENTICADO`; sem permissão → 403 `ACESSO_NEGADO`; policy sem avaliador → exceção).
- **Validação obrigatória** de commands (`RequireValidatorForCommands = true`), `IRequestValidator<T>` e `[SkipValidation]`; todos os validadores rodam e os erros são somados, sem alocação quando a requisição é válida.
- **Exceções:** `AppException` e as reconhecidas por `IExceptionErrorMapper` viram `Result` de falha; as demais seguem para o tratamento global.
- **Transação por command** com `IUnitOfWork` e `[SkipTransaction]`; commit e rollback não canceláveis. Command aninhado que falha faz o pai desfazer a transação inteira e retornar falha (`TRANSACAO_DESFEITA` quando o interno lança); commands com transação em paralelo no mesmo escopo ou na mesma transação são rejeitados.
- **Notificações** (`INotification`, `INotificationHandler<T>`) com `Publish` (dentro da transação) e `PublishAfterCommit` (só após o commit, descartada em falha), handlers polimórficos, ordem determinística e limite de 10 rodadas pós-commit.
- **Verificações de inicialização:** handler duplicado ou registrado à mão com tempo de vida diferente de `Scoped`, marcações contraditórias, `Roles`/`Policy` em branco, authorizers e validadores que nunca seriam executados, behavior registrado antes do `AddTecCqrs`, tipos que não carregam na varredura.
- **Observabilidade sem dependência:** `ActivitySource` e `Meter` `TEC.Cqrs` da BCL (`tec.cqrs.requests`, `tec.cqrs.request.duration`, `tec.cqrs.notifications`, `tec.cqrs.notification.duration`), assinados pelo TEC.Observability; logs com `LoggerMessage` (eventos 1000–1010 e 1016), um único registro por falha, aviso de lentidão (`SlowRequestThreshold`).
- **Sem vazamento:** conteúdo de requisições e notificações nunca registrado; traces só com o tipo da exceção (`RecordExceptionDetailsInTraces = false`).
- **`CqrsDiagnostics`** para testes de arquitetura: `FindRequestsWithoutHandler`, `FindCommandsWithoutValidator`, `FindRequestsWithoutAuthorization` e `IsExceptionLogged`.

#### 🌐 TEC.Cqrs.AspNetCore

- `.AddAspNetCore()`: usuário do `HttpContext.User` e policies pelo `IAuthorizationService`; falha na subida se já houver `IPrincipalAccessor` registrado, com substituição explícita por `ReplaceExistingPrincipalAccessor`.
- `ToHttpResult` e `ToCreatedHttpResult`: `Result` → `ApiResponse`/`PagedResponse` com status pelo `ErrorType`, `traceId` nas falhas e metadados OpenAPI automáticos, para Minimal APIs e Controllers.
- Cabeçalho `Location` restrito a caminhos relativos, escapado por segmento (sem redirecionamento aberto nem injeção de cabeçalho).
- `UseTecExceptionHandler`: exceções → `ApiResponse`, 500/502 sem detalhes, sem logs duplicados (middleware próprio no .NET 8).
- `CqrsAspNetCoreOptions.JsonTypeInfoResolver` para serialização com metadados gerados em compilação (Native AOT).

#### ✅ TEC.Cqrs.FluentValidation

- `.AddFluentValidation()`: executa os `AbstractValidator<T>` no pipeline, inclusive regras assíncronas, com um contexto por validator; erros por campo em camelCase (`CamelCaseValidationFields`).
- Registro por varredura (`RegisterValidatorsFromAssembly`) ou explícito (`AddValidator<TRequest, TValidator>()`, compatível com AOT); falha na subida se a requisição só tiver validator de tipo base ou interface.
- Conversão da `FluentValidation.ValidationException` em falha de validação (pipeline e `UseTecExceptionHandler`).

#### 🧪 Qualidade

- `TEC.Cqrs.Tests` (TUnit): 209 testes em `net10.0` e 216 em `net8.0`, também sem ICU.
- `TEC.Cqrs.LoadTests`: 13 testes `Carga-CI` (concorrência, alocação por `Send`, segurança sob carga, fumaça HTTP) e 7 `Carga-Pesada` (vazão, volume, soak, carga sustentada), com `TEC_CARGA_FATOR` e relatórios por suíte em `TEC_CARGA_RELATORIOS`.
- Samples `TEC.Cqrs.SampleApi` (API de pedidos) e `TEC.Cqrs.LoadGenerator` (gerador de carga HTTP).
- CI/CD pelos workflows reutilizáveis do tec-workflows: `ci.yml`, `release.yml` (com `Carga-Pesada` como portão) e `performance.yml` semanal.

[0.0.1]: https://github.com/tudoemcodigo/lib-tec-cqrs
