# 📝 Changelog

Todas as mudanças relevantes do **TEC.Cqrs** são registradas aqui.
O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa [Versionamento Semântico](https://semver.org/lang/pt-BR/).
Enquanto a versão for `0.x`, mudanças incompatíveis podem ocorrer em versões MINOR.
Os três pacotes (`TEC.Cqrs`, `TEC.Cqrs.AspNetCore` e `TEC.Cqrs.FluentValidation`) saem sempre juntos, com a mesma versão, e devem ser usados na mesma versão.

## [0.0.1] - 2026-10-30

Primeira versão do componente **TEC.Cqrs**, o ecossistema mediator de alta performance, seguro por padrão e compatível com Native AOT.

### ✨ Recursos e Funcionalidades

#### 🧭 TEC.Cqrs (Núcleo)
- **Alvo único .NET 10:** os três pacotes saem só para `net10.0`. Aplicações em .NET 8 não são suportadas. Não há polyfill
  de `System.Threading.Lock` nem equivalentes internos de APIs do .NET 9.
- **Mediator In-Process com Despacho Pré-Compilado (ADR-0001):**
  - Segregação de interfaces: `ISender` (despacho 1:1), `IPublisher` (publicação 1:N) e `IMediator` unificado.
  - Despacho em tempo constante sem reflexão no caminho quente via `RequestHandlerWrapper` e `CqrsRegistry`.
  - Compatibilidade nativa com Native AOT e Trimming.
  - Os authorizers que se aplicam a cada requisição conhecida são calculados no `AddTecCqrs`. O núcleo não usa
    `Type.GetInterfaces`, `MakeGenericType` nem `Activator` em execução para isso, e o código não tem supressões
    IL2090/IL3050 (o `ResultFactory` e o executor de handlers registrados direto no container são protegidos por
    `RuntimeFeature.IsDynamicCodeSupported` de forma que o analisador reconhece).
- **Result Pattern Obrigatório (ADR-0002):**
  - Contratos tipados: `ICommand`, `ICommand<TValue>`, `IQuery<TValue>`, `IRequest<TResponse> where TResponse : Result`.
  - Curto-circuito funcional sem lançamento de exceções custosas em validação, autorização e regras de negócio.
- **Pipeline Estrito e Determinístico (ADR-0003):**
  - Ordem imutável de behaviors: `Logging` ➔ `Exception` ➔ `Authorization` ➔ `Validation` ➔ Custom ➔ `Performance` ➔ `Transaction` ➔ Handler.
  - Bloqueio preventivo na inicialização contra injeção de behaviors desordenados antes do logging e autorização.
- **Autorização em Três Níveis com Fail-Closed (ADR-0004):**
  - Atributos declarativos `[AuthorizeRequest]` (papéis, autenticação e policies via `IRequestPolicyEvaluator`).
  - Permissões granulares `[RequirePermission]` com suporte a `PermissionMatch.All` e `PermissionMatch.Any` (propriedade `Mode`) via `IPermissionChecker`.
  - Autorizadores contextuais de negócio `IRequestAuthorizer<TRequest>` anti-BOLA/IDOR.
  - Verificação na inicialização: requisição sem autorização declarada impede a subida do host (`RequireAuthorization`).
  - Verificação de autenticação obrigatória para authorizers sem atributos (`AuthorizersRequireAuthentication`).
  - Restrição de papéis e permissões exclusivamente a identidades autenticadas.
  - `AuthorizationBehavior`: com authorizers sem atributo, confere a autenticação antes de criar qualquer authorizer; com
    atributos, os authorizers só são criados depois que os atributos autorizam.
  - Authorizer de tipo base ou interface registrado direto no container, antes ou depois do `AddTecCqrs`, é recusado. Com
    `.AddAspNetCore()`, o host não sobe; sem ele, o primeiro `Send` lança `InvalidOperationException`. Registre esses
    authorizers com `AddRequestAuthorizer` ou pela varredura.
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
  - Métricas `tec.cqrs.requests` e `tec.cqrs.request.duration` (requisições) e `tec.cqrs.notifications` e `tec.cqrs.notification.duration` (notificações), com dimensões controladas sem risco de alta cardinalidade.
  - Supressão de logs duplicados para exceções já registradas no pipeline (`LoggedExceptions`).
  - Alerta configurável para requisições lentas via `PerformanceBehavior` (`SlowRequestThreshold`).

#### 🌐 TEC.Cqrs.AspNetCore
- **Mapeamento Seguro e Limpeza de Erros (ADR-0009):**
  - `ApiResponseHttpResult<TResponse>` compatível com Minimal APIs (`IResult`) e Controllers (`IActionResult`).
  - Suporte completo a metadados OpenAPI (`IEndpointMetadataProvider`) para status 200, 201 e falhas 400 a 502.
  - Conversão fluida `ToHttpResult()` e `ToCreatedHttpResult()`.
  - `ToNoContentHttpResult()` (`ApiResponseNoContentHttpResult`: 204 em sucesso, envelope de erro em falha). O
    `StatusCode` usa o mesmo mapeamento do `ApiResponse.FromResult` e coincide com o status escrito na resposta (ex.:
    validação seguida de erro interno: 500).
  - `HttpConcurrencyExtensions` (`GerarETag`, `SetETag`, `ExtrairRowVersion`, `GetIfMatchRowVersion`) para ETag e
    `If-Match` com `rowversion`.
  - Sanitização de URLs no cabeçalho `Location` contra Open Redirect e CRLF injection.
  - Ocultação estrita de detalhes em falhas 500/502 para o cliente HTTP, anexando `traceId` para auditoria.
  - Tratamento global de exceções com `UseTecExceptionHandler`, sobre o `UseExceptionHandler` do ASP.NET Core.
- **Idempotência HTTP por Cabeçalho com Reserva Concorrente (ADR-0008):**
  - Middleware de idempotência `IdempotencyMiddleware` nos endpoints com `[Idempotent]`.
  - Reserva atômica em duas fases (`TryBeginAsync` e `CompleteAsync`/`AbandonAsync`).
  - Escopo criptográfico isolado por usuário/tenant com SHA-256 de tipo, emissor e valor do claim (`ScopeDiscriminator`).
  - Hash SHA-256 do método, rota, query, corpo (até `MaxRequestBodyBytes`) e cabeçalhos selecionados (`HashedRequestHeaders`).
  - Retenção de buffer de resposta e gravação antes do envio ao cliente, mantendo a reserva mesmo em desconexão após commit.
  - Cancelamento pelo cliente: o status 2xx sozinho não segura a chave. Sem transação confirmada e sem resposta de sucesso
    escrita, a reserva é liberada e a repetição executa.
  - Se o store falhar ao guardar a resposta depois de a operação ser executada, o middleware tenta de novo até 3 vezes
    (esperas de 50 e 200 ms). Se ainda falhar, envia a resposta ao cliente e registra o evento 1027 (só o tipo da exceção,
    sem chave nem corpo). As tentativas intermediárias geram o evento 1026.
  - `InMemoryIdempotencyStore` thread-safe com limite global (`MaxEntries`), limite por escopo (`MaxEntriesPerScope`) com
    descarte O(n) e orçamento global, em bytes, das respostas guardadas (`InMemoryIdempotencyStoreOptions.MaxTotalBytes`,
    padrão 256 MiB, validado na subida).
  - Para abrir espaço, o `InMemoryIdempotencyStore` descarta as entradas expiradas de qualquer escopo e, se não bastar, só
    as respostas do próprio escopo de quem está pedindo. Respostas válidas de outros usuários nunca são descartadas: cheio
    de reservas em andamento e respostas válidas de outros escopos, recusa a nova reserva com `InvalidOperationException`.

#### 🧩 TEC.Cqrs.FluentValidation
- **Integração com FluentValidation:**
  - Adaptador `FluentValidationRequestValidator<TRequest>` conectando `IValidator<TRequest>` ao pipeline CQRS.
  - Instâncias isoladas de `ValidationContext` por validador para evitar erros duplicados.
  - Formatação padronizada camelCase para nomes de propriedades (`CamelCaseValidationFields`).
  - Mapeador de exceções `FluentValidationExceptionMapper` convertendo `ValidationException` em `Result` de validação (HTTP 400).

#### 🧪 Testes e CI
- Job "Native AOT" no `ci.yml`: publica o `samples/TEC.Cqrs.AotSample` com `PublishAot` (aviso de trimming/AOT quebra o
  publish) e executa o binário nativo, que confere despacho, autorização por interface, validação, transação, notificação
  pós-commit e JSON.
- O teste de regressão do authorizer de interface (`CqrsIssueRegressionTests`) tem casos separados para o
  `AddRequestAuthorizer`, a recusa do registro direto no container e a recusa na subida do host.
- Teste de concorrência (`ParallelCommand`) menos sujeito a falhas intermitentes no CI.
- `Carga-CI` e `Carga-Pesada` só rodam sob demanda (`performance.yml`), nunca no CI de pull request.

#### 🛠️ Manutenção
- SDK fixado em `10.0.402` (`global.json`, `rollForward: latestPatch`), `Microsoft.NET.ILLink.Tasks` 10.0.14 e lock files
  com os hashes publicados.
- `PackageReleaseNotes` sincronizado com o template do tec-workflows.

#### 📚 Documentação
- Documentação em `docs/` (18 temas, incluindo permissões, idempotência, revisão técnica e os 11 ADRs), READMEs dos três pacotes, dos samples e do CI/CD, conferidos com o código.
