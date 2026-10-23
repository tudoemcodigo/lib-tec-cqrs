# 📝 Changelog

Todas as mudanças relevantes do **TEC.Cqrs** são registradas aqui.
O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/).
Enquanto a versão for `0.x`, mudanças incompatíveis podem ocorrer em versões MINOR.
Os três pacotes (`TEC.Cqrs`, `TEC.Cqrs.AspNetCore` e `TEC.Cqrs.FluentValidation`) saem sempre juntos, com a mesma versão, e devem ser usados na mesma versão.

## [0.0.1] - Não publicado (Protótipo)

Primeira versão do componente **TEC.Cqrs**, o ecossistema mediator de alta performance, seguro por padrão e compatível com Native AOT. Em desenvolvimento e ainda não publicado em produção.

### ✨ Recursos e Funcionalidades

#### 🧭 TEC.Cqrs (Núcleo)
- **Mediator In-Process com Despacho Pré-Compilado (ADR-0001):**
  - Segregação de interfaces: `ISender` (despacho 1:1), `IPublisher` (publicação 1:N) e `IMediator` unificado.
  - Despacho em tempo constante sem reflexão no caminho quente via `RequestHandlerWrapper` e `CqrsRegistry`.
  - Compatibilidade nativa com Native AOT e Trimming (.NET 8 e .NET 10).
- **Result Pattern Obrigatório (ADR-0002):**
  - Contratos tipados: `ICommand`, `ICommand<TValue>`, `IQuery<TValue>`, `IRequest<TResponse> where TResponse : Result`.
  - Curto-circuito funcional sem lançamento de exceções custosas em validação, autorização e regras de negócio.
- **Pipeline Estrito e Determinístico (ADR-0003):**
  - Ordem imutável de behaviors: `Logging` ➔ `Exception` ➔ `Authorization` ➔ `Validation` ➔ Custom ➔ `Performance` ➔ `Transaction` ➔ Handler.
  - Bloqueio preventivo na inicialização contra injeção de behaviors desordenados antes do logging e autorização.
- **Autorização em Três Níveis com Fail-Closed (ADR-0004):**
  - Atributos declarativos `[AuthorizeRequest]` (papéis, autenticação e policies via `IRequestPolicyEvaluator`).
  - Permissões granulares `[RequirePermission]` com suporte a `Match.All` e `Match.Any` via `IPermissionChecker`.
  - Autorizadores contextuais de negócio `IRequestAuthorizer<TRequest>` anti-BOLA/IDOR.
  - Verificação na inicialização: requisição sem autorização declarada impede a subida do host (`RequireAuthorization`).
  - Verificação de autenticação obrigatória para authorizers sem atributos (`AuthorizersRequireAuthentication`).
  - Restrição de papéis e permissões exclusivamente a identidades autenticadas.
- **Validação Obrigatória de Comandos (ADR-0005):**
  - Comandos exigem validador por padrão (`RequireValidatorForCommands`), salvo com `[SkipValidation]`.
  - Abstração pura `IRequestValidator<TRequest>`.
- **Gestão Transacional com Rollback-Only (ADR-0006):**
  - `TransactionBehavior` acoplado ao `IUnitOfWork` (apenas para commands).
  - Participação coordenada de comandos aninhados na transação externa.
  - Comandos internos que falham marcam a transação como `Rollback-Only` (`TRANSACAO_DESFEITA`), impedindo persistências parciais.
  - Detecção e rejeição de concorrência destrutiva no mesmo escopo (`DbContext`).
  - Finalização resiliente com `CancellationToken.None` em `CommitAsync` e `RollbackAsync`.
- **Notificações Imediatas vs Pós-Commit (ADR-0007):**
  - `IPublisher.Publish`: atômica e imediata dentro da transação.
  - `IPublisher.PublishAfterCommit`: fila isolada via `AsyncLocal<RequestFrame>`, descartada em caso de rollback e executada após commit confirmado com isolamento de falhas e limite anti-recursão (`MaxDeferredRounds = 10`).
- **Observabilidade Nativa (ADR-0010):**
  - Spans `ActivitySource` ("TEC.Cqrs") sem exposição de dados sensíveis ou payloads nos traces.
  - Histograma `cqrs.requests.duration` com dimensões controladas sem risco de alta cardinalidade.
  - Supressão de logs duplicados para exceções já registradas no pipeline (`LoggedExceptions`).
  - Alerta configurável para requisições lentas via `PerformanceBehavior` (`SlowRequestThreshold`).

#### 🌐 TEC.Cqrs.AspNetCore
- **Mapeamento Seguro e Limpeza de Erros (ADR-0009):**
  - `ApiResponseHttpResult<TResponse>` compatível com Minimal APIs (`IResult`) e Controllers (`IActionResult`).
  - Suporte completo a metadados OpenAPI (`IEndpointMetadataProvider`) para status 200, 201 e falhas 400 a 502.
  - Conversão fluida `ToHttpResult()` e `ToCreatedHttpResult()`.
  - Sanitização de URLs no cabeçalho `Location` contra Open Redirect e CRLF injection.
  - Ocultação estrita de detalhes em falhas 500/502 para o cliente HTTP, anexando `traceId` para auditoria.
  - Middleware de exceção global com paridade de diagnóstico entre .NET 8 e .NET 10 (`TecExceptionHandlerMiddleware`).
- **Idempotência HTTP por Cabeçalho com Reserva Concorrente (ADR-0008):**
  - Middleware de idempotência `IdempotencyMiddleware` nos endpoints com `[Idempotent]`.
  - Reserva atômica em duas fases (`TryBeginAsync` e `CompleteAsync`/`AbandonAsync`).
  - Escopo criptográfico isolado por usuário/tenant com SHA-256 de tipo, emissor e valor do claim (`ScopeDiscriminator`).
  - Hash SHA-256 do método, rota, query, corpo (até `MaxRequestBodyBytes`) e cabeçalhos selecionados (`HashedRequestHeaders`).
  - Retenção de buffer de resposta e gravação antes do envio ao cliente, mantendo a reserva mesmo em desconexão após commit.
  - `InMemoryIdempotencyStore` thread-safe com limite global (`MaxEntries`) e limite por escopo (`MaxEntriesPerScope`) com descarte $O(n)$.

#### 🧩 TEC.Cqrs.FluentValidation
- **Integração com FluentValidation:**
  - Adaptador `FluentValidationRequestValidator<TRequest>` conectando `IValidator<TRequest>` ao pipeline CQRS.
  - Instâncias isoladas de `ValidationContext` por validador para evitar erros duplicados.
  - Formatação padronizada camelCase para nomes de propriedades (`CamelCaseValidationFields`).
  - Mapeador de exceções `FluentValidationExceptionMapper` convertendo `ValidationException` em `Result` de validação (HTTP 400).
