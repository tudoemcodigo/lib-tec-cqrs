[🏠 TEC.Cqrs](../README.md) › [📚 Documentação](README.md) › Revisão técnica

# 🔎 Revisão técnica

> Revisão de segurança, bugs, implementação, organização e documentação dos três pacotes (`TEC.Cqrs`,
> `TEC.Cqrs.AspNetCore` e `TEC.Cqrs.FluentValidation`). Cada achado foi conferido no código-fonte, com `arquivo:linha`.

## 📑 Sumário

- [🎯 Escopo](#-escopo)
- [🧪 Build e testes](#-build-e-testes)
- [📊 Resumo dos achados](#-resumo-dos-achados)
- [🔴 Alta](#-alta)
- [🟠 Média](#-média)
- [🟡 Baixa](#-baixa)
- [🔵 Info](#-info)
- [✅ Pontos fortes](#-pontos-fortes)
- [🚀 Melhorias sugeridas](#-melhorias-sugeridas)
- [📝 Ajustes na documentação](#-ajustes-na-documentação)
- [🛠️ Correções aplicadas — 2026-10-19](#️-correções-aplicadas--2026-10-19)
- [🔬 Auditoria de Robustez e Alinhamento com o Livro CQRS — 2026-10-23](#-auditoria-de-robustez-e-alinhamento-com-o-livro-cqrs--2026-10-23)

---

## 🎯 Escopo

| Item | Valor |
|---|---|
| Data | 2026-10-19 |
| Commit analisado | `5430927 CHANGELOG: 0.1.0 publicada em 2026-10-09 (#3)` (árvore com `.gitignore` modificado localmente, fora do escopo) |
| Versão | `0.1.0` (`Directory.Build.props`) |
| Código revisado | `TEC.Cqrs/**`, `TEC.Cqrs.AspNetCore/**`, `TEC.Cqrs.FluentValidation/**`, `build/`, `.github/workflows/`, `samples/` (por amostragem), `docs/` |
| Consultados para integração | `TEC.Core` (`ApiResponse`), `TEC.ORM` (`EfIdempotencyStore`, `IdempotencyRecord`), `build/Tec.Build.*` (canônicos do `tec-workflows`) |
| Fora do escopo | Testes de carga (`TEC.Cqrs.LoadTests`) e integração; não há source generator neste componente |

> [!NOTE]
> Documento **histórico**: commit, versões (`0.1.0`, `0.2.0`) e contagens de testes são os do momento de cada revisão. A
> versão em desenvolvimento é a `0.0.2` (`Directory.Build.props`; última estável: `0.0.1`, ver [CHANGELOG](../CHANGELOG.md))
> e os achados corrigidos já fazem parte dela.
> A pendência do B-04 (collation binária no TEC.ORM) foi resolvida: o `EfIdempotencyStore` do `TEC.ORM.SqlServer` já usa
> `Latin1_General_100_BIN2` em `Scope` e `IdempotencyKey`.

Severidades: **Crítica** (exploração direta, impacto amplo) · **Alta** (perda de garantia central ou dado) · **Média**
(falha de segurança ou correção sob condições plausíveis) · **Baixa** (condições restritas ou impacto limitado) ·
**Info** (melhoria, higiene).

---

## 🧪 Build e testes

| Etapa | Comando | Resultado |
|---|---|---|
| Restore em modo pacote (padrão) | `dotnet build TEC.Cqrs.slnx` | Não executado com sucesso nesta máquina: o feed `tec-interno` exige credencial (NU1301/401) e o `TEC.Core` não está no cache local |
| Build (modo local, `TEC.Core` vizinho) | `dotnet build TEC.Cqrs.Tests -c Release -p:TecUseLocalProjects=true` | ✅ **0 avisos, 0 erros** em `net8.0` e `net10.0` (os três pacotes com `TreatWarningsAsErrors` e `IsAotCompatible`) |
| Testes unitários `net10.0` | `dotnet test --project TEC.Cqrs.Tests -c Release --no-build` | ✅ **255/255 aprovados** (2,6 s) |
| Testes unitários `net8.0` | idem | ⚠️ Não executados: runtime `Microsoft.AspNetCore.App 8.0` ausente na máquina (só 10.0.12) |
| Prova de conceito do achado A-01 | Programa descartável fora do repositório, usando as DLLs compiladas | Confirmou a reexecução (detalhes no achado) |

> [!NOTE]
> No modo local o build usa o código atual do `TEC.Core` vizinho (versão `0.1.0`), não o pacote `0.0.1` fixado em
> `Directory.Packages.props`, que é o que o CI e os consumidores recebem.

---

## 📊 Resumo dos achados

| ID | Severidade | Categoria | Local | Resumo | Status (2026-10-19) |
|---|---|---|---|---|---|
| A-01 | 🔴 Alta | Bug / idempotência | `TEC.Cqrs.AspNetCore/Idempotency/IdempotencyMiddleware.cs:111-115` | Cliente que desconecta depois do commit faz a chave ser liberada; a repetição executa a operação de novo | ✅ Corrigido |
| M-01 | 🟠 Média | Segurança / autorização | `TEC.Cqrs/Behaviors/AuthorizationBehavior.cs:40-66` | Requisição protegida só por `IRequestAuthorizer` não exige autenticação | ✅ Corrigido (breaking) |
| M-02 | 🟠 Média | Segurança / idempotência | `IdempotencyMiddleware.cs:154-168`, `:174` | Escopo da chave é só o valor do id do usuário (sem tipo de claim, emissor ou tenant) e o hash ignora cabeçalhos: replay entre tenants/esquemas | ✅ Corrigido (breaking) |
| M-03 | 🟠 Média | Segurança / idempotência | `IdempotencyMiddleware.cs:54-61` | Endpoint com `keyRequired` executa **sem** proteção quando o usuário não tem claim de id; só log Debug | ✅ Corrigido (breaking) |
| B-01 | 🟡 Baixa | Bug / notificações | `TEC.Cqrs/Internal/Mediator.cs:63`, `:112` | Deduplicação por tipo da classe descarta registros distintos da mesma classe (factories) | ✅ Corrigido (breaking) |
| B-02 | 🟡 Baixa | Segurança (DoS) / desempenho | `TEC.Cqrs/Idempotency/InMemoryIdempotencyStore.cs:99-118` | Capacidade global: um usuário despeja as respostas guardadas dos outros; despejo O(n log n) por inserção | ✅ Corrigido |
| B-03 | 🟡 Baixa | Segurança / autorização | `TEC.Cqrs/Authorization/IPermissionChecker.cs:59`, `AuthorizationBehavior.cs:101` | Permissões e papéis lidos de todas as identidades do principal, inclusive não autenticadas | ✅ Corrigido |
| B-04 | 🟡 Baixa | Contrato / idempotência | `TEC.Cqrs/Idempotency/IIdempotencyStore.cs:17-39` | Sensibilidade a maiúsculas da chave/escopo não definida: InMemory é ordinal, o store do TEC.ORM segue a collation do banco | 🟡 Corrigido no Cqrs (contrato); collation pendente no TEC.ORM |
| B-05 | 🟡 Baixa | API | `IIdempotencyStore.cs:46-64` | `IdempotencyKey` é `record struct`: `default`/`new()` contornam a validação | ✅ Corrigido |
| B-06 | 🟡 Baixa | Validação / fail-fast | `TEC.Cqrs/Behaviors/ValidationBehavior.cs:28-36` | Command sem validador só é detectado na primeira execução em produção | ✅ Corrigido (com `.AddAspNetCore()`) |
| B-07 | 🟡 Baixa | Bug / idempotência | `IdempotencyMiddleware.cs:32` | Cabeçalho com vários valores e o primeiro vazio é tratado como ausente | ✅ Corrigido |
| B-08 | 🟡 Baixa | Organização / release | `Directory.Build.props:6` | `<Version>` continua `0.1.0` após publicar a 0.1.0: as prévias saem como `0.1.0-preview.N` (menores que a estável) | ✅ Corrigido (`0.2.0`) |
| I-01 | 🔵 Info | HTTP | `TEC.Cqrs.AspNetCore/ApiResponseHttpResult.cs:123-141` | 401 do pipeline sai sem `WWW-Authenticate` | ⏸️ Não corrigido |
| I-02 | 🔵 Info | Desempenho | `IdempotencyMiddleware.cs:180`, `:206`; `InMemoryIdempotencyStore.cs:121-125` | Até 3 cópias de 1 MiB por resposta idempotente e buffer de 16 KiB por requisição | 🟡 Parcial |
| I-03 | 🔵 Info | Testes | `TEC.Cqrs.Tests/` | Lacunas de testes nos cenários acima; `net8.0` depende do runtime 8 no agente | ✅ Corrigido (testes) |
| I-04 | 🔵 Info | Dependências | `Directory.Packages.props` | `Microsoft.Extensions.Logging.Abstractions 10.0.12` também no alvo `net8.0`; `TEC.Core` fixado em `0.0.1` | ⏸️ Não corrigido |

Contagem: **Crítica 0 · Alta 1 · Média 3 · Baixa 8 · Info 4**.

---

## 🔴 Alta

### A-01 — Desconexão do cliente depois do commit libera a chave idempotente

**Problema.** O middleware libera a reserva (`AbandonAsync`) em **qualquer** exceção do restante do pipeline. Quando o
cliente desconecta (timeout do cliente, queda de rede) depois de o command ser confirmado, a escrita da resposta lança
`OperationCanceledException`, porque o `ApiResponseWriter` serializa com o `RequestAborted`. A chave é liberada e a
repetição do cliente — exatamente o cenário para o qual a idempotência existe — executa a operação de novo.

**Evidência.**

`TEC.Cqrs.AspNetCore/Idempotency/IdempotencyMiddleware.cs:107-115`

```csharp
try
{
    await next(context).ConfigureAwait(false);
}
catch
{
    // A chave é liberada: o cliente pode tentar de novo
    await store.AbandonAsync(key, lockId, CancellationToken.None).ConfigureAwait(false);
    throw;
}
```

`TEC.Cqrs.AspNetCore/ApiResponseHttpResult.cs:141`

```csharp
return httpContext.Response.WriteAsJsonAsync(response, typeInfo, ContentType, httpContext.RequestAborted);
```

Como o token é informado explicitamente (`CanBeCanceled = true`), o ASP.NET Core não engole o
`OperationCanceledException` (ele só faz isso quando nenhum token é passado). O `TransactionBehavior` confirma com
`CancellationToken.None` (`TransactionBehavior.cs:156`), então o commit acontece mesmo com o cliente já desconectado.

**Prova.** Um programa descartável (fora do repositório) com `InMemoryIdempotencyStore`, um endpoint que conta a execução,
cancela o `RequestAborted` (simulando a desconexão após o commit) e responde com `ToHttpResult()`:

```text
1a: TaskCanceledException execucoes=1 entradas=0
2a: TaskCanceledException execucoes=2 entradas=0
```

A segunda requisição com a mesma chave e o mesmo corpo executou a operação de novo.

**Impacto.** Pedido ou pagamento duplicado quando o cliente estoura o próprio timeout durante uma operação lenta e repete
com a mesma `Idempotency-Key`. Qualquer outra exceção depois do commit (ex.: falha ao montar o `Location`) tem o mesmo
efeito.

**Correção sugerida.** Desacoplar a execução da entrega: nos endpoints idempotentes, bufferizar a resposta (ela já é
limitada por `MaxResponseBodyBytes`), guardar no store **antes** de enviar ao cliente e enviar sem depender do
`RequestAborted`. Em paralelo, não liberar a chave quando a falha é o cancelamento pelo cliente depois de um status 2xx.

```csharp
// CaptureStream em modo "só buffer": nada vai para o cliente durante o next()
var capture = new CaptureStream(inner: Stream.Null, _options.MaxResponseBodyBytes);
context.Response.Body = capture;
try
{
    await next(context).ConfigureAwait(false);
}
catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested
                                         && context.Response.StatusCode is >= 200 and <= 299)
{
    // A operação pode ter sido confirmada: mantém a reserva (a repetição recebe 409 até o LockTimeout)
    IdempotencyLog.AbortedAfterSuccess(logger);
    throw;
}
catch
{
    await store.AbandonAsync(key, lockId, CancellationToken.None).ConfigureAwait(false);
    throw;
}
finally
{
    context.Response.Body = original;
}

// guarda primeiro (CompleteAsync) e só então copia o buffer para o cliente
await store.CompleteAsync(key, lockId, response, CancellationToken.None).ConfigureAwait(false);
await original.WriteAsync(capture.Captured, CancellationToken.None).ConfigureAwait(false);
```

No `ApiResponseWriter`, serializar para bytes (`JsonSerializer.SerializeToUtf8Bytes(response, typeInfo)`) e escrever
sem o `RequestAborted` evita também guardar um corpo truncado. Acrescentar um teste de regressão com o cenário da prova.

---

## 🟠 Média

### M-01 — Requisição protegida só por `IRequestAuthorizer` não exige autenticação

**Problema.** Quando a requisição não tem `[AuthorizeRequest]` nem `[RequirePermission]`, o behavior pula a verificação
de usuário e chama só os authorizers. Com `RequireAuthorization` ativo, o authorizer conta como "autorização declarada",
mas o pipeline não garante que exista usuário autenticado: tudo depende de cada authorizer tratar o `Principal` nulo.

**Evidência.** `TEC.Cqrs/Behaviors/AuthorizationBehavior.cs:40-66`

```csharp
if (rules.Length == 0 && permissionRules.Length == 0 && authorizers.Length == 0) { ... }

if ((rules.Length > 0 || permissionRules.Length > 0)
    && await AuthorizeAttributesAsync(...) is { } denied)   // único ponto que exige autenticação (linha 101)
    return ResultFactory<TResponse>.Failure([denied]);

foreach (var (target, authorizer) in authorizers) { ... }    // roda também para anônimos
```

O exemplo de authorizer por interface da documentação (`autorizacao.md`, `CustomerOrderAuthorizer`) passa
`user.Principal?.FindFirst("cliente_id")?.Value` — possivelmente `null` — direto ao repositório.

**Impacto.** Um authorizer escrito como "nega se o pedido pertence a outro cliente" (em vez de "permite se pertence a
este") autoriza o anônimo; um repositório que trata `clienteId == null` como "sem filtro" também. O caso de uso fica
acessível sem login por qualquer ponto de entrada (HTTP sem `.RequireAuthorization()`, fila, job).

**Correção sugerida.** Tornar a autenticação implícita sempre que houver authorizer e a requisição não for
`[AllowAnonymousRequest]`, com uma opção para quem precisa do comportamento atual (mudança incompatível aceitável em `0.x`):

```csharp
bool requiresUser = rules.Length > 0 || permissionRules.Length > 0
    || (authorizers.Length > 0 && options.AuthorizersRequireAuthentication && !RequestInfo<TRequest>.AllowAnonymous);

if (requiresUser && serviceProvider.GetService<IPrincipalAccessor>()?.Principal?.Identity?.IsAuthenticated != true)
    return ResultFactory<TResponse>.Failure([NotAuthenticated]);
```

Enquanto isso, a documentação foi ajustada (ver [Ajustes na documentação](#-ajustes-na-documentação)).

### M-02 — Escopo da chave idempotente sem tipo de claim, emissor ou tenant

**Problema.** O escopo é o **valor** do primeiro claim encontrado em `UserIdClaimTypes` (`tec_uid`, `oid`,
`NameIdentifier`, `sub`), sem o tipo do claim, o emissor ou o tenant. O hash cobre método, caminho, query e corpo, mas
nenhum cabeçalho.

**Evidência.** `IdempotencyMiddleware.cs:160-165` e `:174`

```csharp
var id = user.FindFirst(type)?.Value;
if (IdempotencyKey.IsValid(id, IdempotencyKey.MaxScopeLength))
    return id;                                   // só o valor
...
hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));
```

**Impacto.**

- Aplicação multi-tenant com o tenant num cabeçalho (ou em claim não usado no escopo): o mesmo usuário repetindo a mesma
  chave (chaves determinísticas como `pedido-42` são comuns) no tenant B recebe a resposta guardada do tenant A — vazamento
  entre tenants — e a operação no tenant B **não** é executada.
- Vários esquemas de autenticação (ex.: usuário Entra com `oid` e API key com `NameIdentifier`) cujos valores coincidem
  compartilham o espaço de chaves.

**Correção sugerida.** Compor o escopo com o tipo do claim e o emissor, e permitir que a aplicação o estenda (tenant) e
inclua cabeçalhos no hash:

```csharp
// IdempotencyOptions
public Func<HttpContext, string?>? ScopeResolver { get; set; }          // ex.: $"{tenant}|{userId}"
public ISet<string> HashedRequestHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// padrão
var claim = user.FindFirst(type);
return claim is null ? null : $"{type}|{claim.Issuer}|{claim.Value}";  // validar o tamanho (MaxScopeLength) ou usar hash
```

Mudar o formato do escopo invalida as chaves guardadas (aceitável em `0.x`; registrar no CHANGELOG).

### M-03 — `keyRequired` não garante idempotência quando o usuário não tem claim de id

**Problema.** Com a chave presente e sem escopo resolvido (usuário anônimo **ou** autenticado sem nenhum dos
`UserIdClaimTypes`, comum em API keys e identidades de serviço), o middleware executa o endpoint sem reserva. Só registra
o evento 1022 em nível **Debug**, invisível em produção. Isso vale mesmo para endpoints marcados com `keyRequired: true`.

**Evidência.** `IdempotencyMiddleware.cs:54-61` e `TEC.Cqrs.AspNetCore/Internal/IdempotencyLog.cs:23`

```csharp
string? scope = ResolveScope(context);
if (scope is null)
{
    IdempotencyLog.AnonymousRequest(logger);   // LogLevel.Debug
    await next(context).ConfigureAwait(false);
    return;
}
```

**Impacto.** O cliente envia a chave num endpoint de pagamento marcado como obrigatório, acredita estar protegido e tem a
operação duplicada na repetição. A inconsistência também aparece no anônimo: sem a chave recebe 400
(`IDEMPOTENCY_KEY_OBRIGATORIA`), com a chave executa sem proteção.

**Correção sugerida.** Com `KeyRequired`, recusar (401 se anônimo; 500/erro de configuração se autenticado sem claim de
id) em vez de executar; sem `KeyRequired`, manter a passagem, mas subir o log para `Warning` quando o usuário estiver
autenticado (indica `UserIdClaimTypes` mal configurado).

---

## 🟡 Baixa

### B-01 — Deduplicação de notification handlers por tipo descarta registros legítimos

**Problema.** A publicação executa cada **classe** de handler uma única vez (`executed.Add(handler.GetType())`), também
dentro do mesmo tipo de notificação. A intenção documentada é não repetir a classe que trata o tipo concreto e uma
interface base; o efeito colateral é ignorar um segundo registro da mesma classe.

**Evidência.** `TEC.Cqrs/Internal/Mediator.cs:61-64` (e `:112` na pós-commit)

```csharp
foreach (var handler in target.Resolve(serviceProvider))
{
    if (handler is not null && executed.Add(handler.GetType()))
        await target.Invoke(handler, notification, cancellationToken).ConfigureAwait(false);
}
```

**Impacto.** `services.AddScoped<INotificationHandler<PedidoCriado>>(_ => new WebhookHandler("url-a"))` e outro com
`"url-b"`: só o primeiro roda, sem erro nem log.

**Correção sugerida.** Deduplicar apenas entre alvos (tipos base/interfaces), não dentro do mesmo alvo:

```csharp
HashSet<Type> seenInPreviousTargets = [];
foreach (var target in targets)
{
    List<Type>? current = null;
    foreach (var handler in target.Resolve(serviceProvider))
    {
        if (handler is null || seenInPreviousTargets.Contains(handler.GetType()))
            continue;
        (current ??= []).Add(handler.GetType());
        await target.Invoke(handler, notification, cancellationToken).ConfigureAwait(false);
    }
    if (current is not null)
        seenInPreviousTargets.UnionWith(current);
}
```

### B-02 — `InMemoryIdempotencyStore`: capacidade global e despejo caro

**Problema.** O limite `MaxEntries` é global. Cheio, o store remove as respostas concluídas que expiram primeiro, de
qualquer usuário; com todas em andamento, lança `InvalidOperationException` (HTTP 500). Cada inserção com o store cheio
percorre e ordena todas as entradas.

**Evidência.** `TEC.Cqrs/Idempotency/InMemoryIdempotencyStore.cs:104-118`

```csharp
foreach (var expired in _entries.Where(e => !e.Value.IsAlive(now)).Select(e => e.Key).ToList())
    _entries.Remove(expired);
...
var oldest = _entries.Where(e => e.Value.Response is not null).OrderBy(e => e.Value.ExpiresAt)...;
```

**Impacto.** Um usuário autenticado que envia milhares de chaves distintas apaga as respostas guardadas dos demais (as
repetições deles executam de novo) e gera custo O(n log n) sob o `lock` global. O store é documentado para uma instância,
testes e desenvolvimento, o que limita a severidade.

**Correção sugerida.** Cota por escopo (ex.: `MaxEntriesPerScope`), despejo por fila de prioridade/LRU e limpeza
periódica das expiradas fora do caminho de inserção.

### B-03 — Claims de identidades não autenticadas contam para permissões e papéis

**Problema.** A autenticação é verificada só na identidade principal (`principal.Identity`), mas o `ClaimPermissionChecker`
procura o claim em **todas** as identidades (`ClaimsPrincipal.FindAll`), e `IsInRole` também percorre todas.

**Evidência.** `TEC.Cqrs/Behaviors/AuthorizationBehavior.cs:100-102` e `TEC.Cqrs/Authorization/IPermissionChecker.cs:59`

```csharp
if (principal?.Identity?.IsAuthenticated != true) return NotAuthenticated;   // só a primeira identidade
...
foreach (var claim in principal.FindAll(_claimType))                          // todas as identidades
```

**Impacto.** Se algum middleware ou `IClaimsTransformation` anexar uma identidade não autenticada com claims vindos da
requisição (cabeçalho, cookie de preferência), um `tec_perm` forjado nela passa na regra. Depende de código da aplicação.

**Correção sugerida.** Ler permissões só de identidades autenticadas:
`principal.Identities.Where(i => i.IsAuthenticated).SelectMany(i => i.FindAll(_claimType))`. Documentar o mesmo para
`Roles` (ou verificar papéis só nas identidades autenticadas).

### B-04 — Contrato do `IIdempotencyStore` não define comparação de chave e escopo

**Problema.** O `InMemoryIdempotencyStore` compara `IdempotencyKey` de forma ordinal (igualdade padrão do `record struct`);
o `EfIdempotencyStore` do TEC.ORM usa `Scope`/`IdempotencyKey` como chave primária `nvarchar` sem collation explícita
(`TEC.ORM/TEC.ORM.SqlServer/Idempotency/IdempotencyRecord.cs:69-71`), seguindo a collation do banco (em geral
case-insensitive).

**Evidência.** `TEC.Cqrs/Idempotency/IIdempotencyStore.cs:17-39` (nenhuma regra de comparação no contrato).

**Impacto.** Comportamento diferente entre desenvolvimento e produção: `Pedido-1` e `pedido-1` são chaves distintas em
memória e a mesma no SQL Server; escopos que diferem só por maiúsculas (ids não normalizados) colidem no banco.

**Correção sugerida.** Definir no contrato que a comparação é ordinal e aplicar collation binária (`Latin1_General_100_BIN2`)
nas colunas do TEC.ORM.

### B-05 — `IdempotencyKey` pode ser criada sem validação

**Problema.** `IdempotencyKey` é `readonly record struct` com validação só no construtor com parâmetros; `default` e
`new IdempotencyKey()` produzem `Scope`/`Key` nulos.

**Evidência.** `TEC.Cqrs/Idempotency/IIdempotencyStore.cs:46-64`.

**Impacto.** Stores próprios recebem chaves nulas (exceção no banco ou chave "vazia" compartilhada). Baixo, pois o
middleware sempre usa o construtor validado.

**Correção sugerida.** Validar no início de `TryBeginAsync`/`CompleteAsync`/`AbandonAsync` (ex.: `key.Scope is null` →
`ArgumentException`) ou trocar para `sealed record` (classe).

### B-06 — Command sem validador só falha na primeira execução

**Problema.** `RequireValidatorForCommands` é verificado no `ValidationBehavior`, em execução; a verificação equivalente de
autorização acontece no `AddTecCqrs`.

**Evidência.** `TEC.Cqrs/Behaviors/ValidationBehavior.cs:28-36`

```csharp
if (_validators.Length == 0)
{
    if (options.RequireValidatorForCommands && RequestInfo<TRequest>.RequiresValidator)
        throw new InvalidOperationException($"O command '{typeof(TRequest).FullName}' não possui validator. ...");
```

**Impacto.** É fail closed (não executa sem validar), mas o erro aparece em produção, no primeiro uso do command, como
HTTP 500. O `CqrsDiagnostics.FindCommandsWithoutValidator` existe, mas depende de a aplicação escrever o teste.

**Correção sugerida.** Validar na subida: os validadores só ficam completos depois do `.AddFluentValidation()`, então a
verificação pode rodar num `IHostedService`/`IStartupFilter` registrado pelo `AddTecCqrs` (ou num
`ValidateOnStart` sobre `CqrsOptions`), usando `RegisteredServices` sobre o `IServiceCollection` capturado.

### B-07 — Cabeçalho com vários valores e o primeiro vazio é tratado como ausente

**Problema.** A ausência é testada só pelo primeiro valor; a regra "um único valor" só é aplicada depois.

**Evidência.** `IdempotencyMiddleware.cs:31-45`

```csharp
if (values.Count == 0 || string.IsNullOrEmpty(values[0]))   // ["", "k1"] cai aqui
```

**Impacto.** `Idempotency-Key: , k1` executa sem idempotência (endpoint opcional) ou recebe
`IDEMPOTENCY_KEY_OBRIGATORIA` (endpoint obrigatório), em vez de `IDEMPOTENCY_KEY_INVALIDA`.

**Correção sugerida.** Tratar como ausente só `values.Count == 0`; qualquer outra forma passa pela validação de valor único.

### B-08 — Versão não avançada depois da publicação da 0.1.0

**Problema.** O CHANGELOG registra a 0.1.0 publicada em 2026-10-09 e o `release.yml` orienta subir a `<Version>` depois de
publicar, mas ela continua `0.1.0`.

**Evidência.** `Directory.Build.props:6` (`<Version>0.1.0</Version>`); `.github/workflows/release.yml` (comentário do
topo); `CHANGELOG.md` (`## [0.1.0] - 2026-10-09`, sem seção "Não publicado").

**Impacto.** Cada push na `main` publica `0.1.0-preview.N`, que pela SemVer é **anterior** à `0.1.0` estável: consumidores
com versão flutuante não recebem as prévias, e prévias com código novo parecem mais antigas que a estável.

**Correção sugerida.** Subir para `0.1.1` (ou `0.2.0`) e abrir a seção `## [Não publicado]` no CHANGELOG.

---

## 🔵 Info

### I-01 — 401 sem `WWW-Authenticate`

`ApiResponseWriter.WriteAsync` (`TEC.Cqrs.AspNetCore/ApiResponseHttpResult.cs:123-141`) define o status a partir do
`Result`; um `Unauthorized` do pipeline vira 401 sem o cabeçalho `WWW-Authenticate` exigido pela RFC 9110. Sugestão: para
401, chamar `httpContext.ChallengeAsync()` quando houver esquema padrão, ou acrescentar o cabeçalho do esquema configurado.

### I-02 — Alocações no caminho idempotente

`CaptureStream` acumula até 1 MiB num `MemoryStream`, `Captured` copia com `ToArray()` (`IdempotencyMiddleware.cs:206`) e
o `InMemoryIdempotencyStore` copia de novo (`InMemoryIdempotencyStore.cs:121-125`); `ComputeHashAsync` aloca 16 KiB por
requisição (`IdempotencyMiddleware.cs:180`). Sugestão: `ArrayPool<byte>` para o buffer do hash e transferência de posse do
buffer capturado (sem a segunda cópia quando o store é o próprio componente).

### I-03 — Lacunas de testes

A suíte é ampla (255 testes, incluindo segurança e regressões), mas não cobre: desconexão depois do commit (A-01),
authorizer sozinho com usuário anônimo (M-01), `keyRequired` com usuário sem claim de id (M-03), dois registros da mesma
classe de notification handler (B-01) e cabeçalho com vários valores (B-07). Os testes `net8.0` exigem o runtime 8 do
ASP.NET Core no agente; localmente só o `net10.0` rodou.

### I-04 — Dependências

- `Microsoft.Extensions.Logging.Abstractions 10.0.12` vale também para o alvo `net8.0`
  (`TEC.Cqrs/packages.lock.json`), levando `System.Diagnostics.DiagnosticSource 10.x` a consumidores .NET 8. É suportado,
  mas força a linha 10 dos `Microsoft.Extensions.*` em apps LTS 8; considerar `8.0.x` condicionado ao TFM, se houver
  consumidores sensíveis a isso.
- `TEC.Core` está fixado em `0.0.1` enquanto o repositório vizinho já está em `0.1.0`: confirmar se a 0.1.0 do TEC.Core foi
  publicada e atualizar (o build local usou o código 0.1.0, que não é o que o CI testa).

---

## ✅ Pontos fortes

- Fail closed consistente: `RequireAuthorization` na subida, `Roles`/`Policy` em branco rejeitados, authorizers e
  validadores "órfãos" derrubam a inicialização, policy sem avaliador lança exceção.
- Pipeline com estado por fluxo assíncrono (`AsyncLocal` no `PipelineContext`), rejeição de `Send` concorrente com
  transação e de commands internos em paralelo, rollback-only para falhas aninhadas.
- Commit, rollback e handlers pós-commit não cancelados pelo cliente; limite de rodadas pós-commit.
- Telemetria sem conteúdo de requisição e com `exception.type` apenas, por padrão.
- Registro explícito compatível com Native AOT; varredura anotada com `RequiresUnreferencedCode`/`RequiresDynamicCode`;
  build dos pacotes com todos os avisos como erro.

---

## 🚀 Melhorias sugeridas

Em ordem de prioridade:

1. **Idempotência à prova de desconexão** (A-01): resposta bufferizada, `CompleteAsync` antes do envio, sem dependência do
   `RequestAborted` depois do commit; teste de regressão.
2. **Escopo idempotente extensível** (M-02/M-03): `IdempotencyOptions.ScopeResolver` (tenant), escopo com tipo de claim e
   emissor, `HashedRequestHeaders`, e `keyRequired` que recusa quando não há escopo.
3. **Autenticação implícita para authorizers** (M-01): `CqrsOptions.AuthorizersRequireAuthentication` (padrão `true`).
4. **Verificação de validadores na subida** (B-06): mesma experiência do `RequireAuthorization`, com a lista de commands
   pendentes.
5. **Source generator de registro** (opcional, AOT): gerar `AddCommandHandler`/`AddQueryHandler`/`AddNotificationHandler`
   a partir do assembly em tempo de compilação, eliminando a escolha entre varredura (reflexão) e registro manual, e
   permitindo diagnósticos de compilação (requisição sem autorização, command sem validador). O TEC.ORM já tem precedente
   (`TEC.ORM.Mapping.Generator`).
6. **Métricas de idempotência** no `Meter` `TEC.Cqrs` (`tec.cqrs.idempotency.requests` com `outcome` = `started`,
   `replayed`, `in_progress`, `mismatch`, `bypassed`): hoje só há logs.
7. **Timeout opcional para handlers pós-commit** (`CqrsOptions.DeferredNotificationTimeout`): eles rodam sem token e podem
   segurar a resposta indefinidamente.
8. **Store distribuído leve** (ex.: `TEC.Cqrs.Idempotency.Redis` com `SET NX PX`), para quem não usa o TEC.ORM.

---

## 📝 Ajustes na documentação

Correções aplicadas nesta revisão (doc dizia X, código faz Y):

| Arquivo | Ajuste |
|---|---|
| [🔑 autorizacao.md](autorizacao.md) | `[AllowAnonymousRequest]` também não pode ser combinado com `[RequirePermission]` (`RequestInfo.cs`, `EnsureValid`) |
| [🔑 autorizacao.md](autorizacao.md) | Explicitado que um authorizer sozinho **não** exige autenticação (`AuthorizationBehavior.cs:53-66`) |
| [🔑 autorizacao.md](autorizacao.md) | Tabela de opções: `RequireAuthorization` também aceita `[RequirePermission]` |
| [🛡️ seguranca.md](seguranca.md) | Checklist: incluído `[RequirePermission]` e o item sobre authorizers sem atributo |
| [📚 README.md](README.md) | Convenção "configuração inválida falha na inicialização": listadas as exceções verificadas só em execução (validador ausente, policy sem avaliador, handler registrado direto no container); tema 17 e mapa |

Sugestões que dependiam das correções — **aplicadas em 2026-10-19** (ver a seção seguinte):

- [🔁 idempotencia.md](idempotencia.md): novo comportamento de `keyRequired` sem escopo (M-03), escopo com tipo/emissor e
  `ScopeDiscriminator`/`HashedRequestHeaders` (M-02), resposta guardada antes do envio e reserva mantida na desconexão
  (A-01), comparação ordinal do contrato (B-04), cota por escopo (B-02).
- [🛡️ seguranca.md](seguranca.md): idempotência e os demais achados no modelo de ameaças, nas garantias e no checklist.
- [🔑 autorizacao.md](autorizacao.md): exemplo `CustomerOrderAuthorizer` nega `cliente_id` ausente; authorizer sozinho
  agora exige autenticação (M-01); papéis só de identidades autenticadas (B-03). O texto "um authorizer sozinho **não** exige
  autenticação", acrescentado nesta revisão, foi substituído.

---

## 🛠️ Correções aplicadas — 2026-10-19

Branch `fix/revisao-tecnica-2026-10-19` (sem commit). Versão `0.2.0` em `Directory.Build.props` (MINOR por haver mudanças
incompatíveis em `0.x`); entrada `## [Não publicado]` no [CHANGELOG](../CHANGELOG.md). Cada correção tem teste de regressão
escrito antes e visto falhar (exceto onde indicado).

| ID | Status | Arquivos alterados | Testes adicionados |
|---|---|---|---|
| A-01 | ✅ Corrigido | `TEC.Cqrs.AspNetCore/Idempotency/IdempotencyMiddleware.cs` (resposta em buffer até `MaxResponseBodyBytes`, `CompleteAsync` antes do envio, envio sem `RequestAborted`; `OperationCanceledException` com `RequestAborted` cancelado depois de 2xx mantém a reserva); `ApiResponseHttpResult.cs` (`ApiResponseWriter` serializa para bytes e escreve sem `RequestAborted`); `Internal/IdempotencyLog.cs` (evento 1023) | `Client_disconnect_after_commit_does_not_release_the_key` (o cenário da prova), `Cancellation_by_the_client_after_success_status_keeps_the_reservation`, `Response_is_stored_before_being_sent_to_the_client`. Conferido também com Kestrel real (programa descartável fora do repositório): cliente que desiste por timeout e repete recebe a resposta guardada, 1 execução |
| M-01 | ✅ Corrigido (breaking) | `TEC.Cqrs/DependencyInjection/CqrsOptions.cs` (`AuthorizersRequireAuthentication`, padrão `true`); `Behaviors/AuthorizationBehavior.cs` | `RequestAuthorizer_alone_requires_an_authenticated_user`, `RequestAuthorizer_runs_for_anonymous_when_AuthorizersRequireAuthentication_is_disabled`, `AuthorizersRequireAuthentication_is_enabled_by_default`. 10 testes existentes que chamavam authorizers sem usuário passaram a usar um usuário autenticado |
| M-02 | ✅ Corrigido (breaking) | `IdempotencyMiddleware.cs` (escopo = SHA-256 de tipo, emissor e valor do claim, só de identidades autenticadas, + discriminador; cabeçalhos no hash); `Idempotency/IdempotencyOptions.cs` (`ScopeDiscriminator`, `HashedRequestHeaders`) | `Same_user_id_value_from_other_claim_type_or_issuer_is_another_scope`, `User_id_from_unauthenticated_identity_is_ignored`, `ScopeDiscriminator_separates_tenants_of_the_same_user`, `HashedRequestHeaders_are_part_of_the_request_content` |
| M-03 | ✅ Corrigido (breaking) | `IdempotencyMiddleware.cs` (`keyRequired` sem escopo: 401 anônimo / 500 `IDEMPOTENCIA_SEM_ESCOPO`); `IdempotencyLog.cs` (1024 Warning para autenticado sem claim em endpoint opcional; 1025 Error) | `Required_key_with_anonymous_user_returns_401_without_executing`, `Required_key_with_user_without_id_claim_returns_500_without_executing` |
| B-01 | ✅ Corrigido (breaking) | `TEC.Cqrs/Internal/Mediator.cs` (deduplicação só entre tipos, em `Publish` e pós-commit) | `Publish_executes_every_registration_of_the_same_handler_class`, `PublishAfterCommit_executes_every_registration_of_the_same_handler_class` (fake `WebhookHandler<T>`) |
| B-02 | ✅ Corrigido | `TEC.Cqrs/Idempotency/InMemoryIdempotencyStore.cs` (`MaxEntriesPerScope`, padrão 1.000, índice por escopo, despejo O(n) sem ordenação); `DependencyInjection/IdempotencyServiceCollectionExtensions.cs` | `Scope_quota_evicts_only_the_entries_of_the_same_scope`, `Scope_quota_with_all_entries_in_progress_refuses_only_that_scope`, `Invalid_scope_quota_is_rejected`. Não feito: limpeza periódica fora do caminho de inserção (a cota por escopo e a passagem única já limitam o custo; um timer no núcleo seria escopo extra) |
| B-03 | ✅ Corrigido | `TEC.Cqrs/Authorization/IPermissionChecker.cs` (`ClaimPermissionChecker` só em identidades autenticadas); `AuthorizationBehavior.cs` (papéis: com todas as identidades autenticadas usa `IsInRole` do principal; senão, só as autenticadas) | `Permission_from_unauthenticated_identity_is_ignored`, `Role_from_unauthenticated_identity_is_ignored` |
| B-04 | 🟡 Corrigido no Cqrs | `TEC.Cqrs/Idempotency/IIdempotencyStore.cs` (contrato: comparação ordinal; bancos com collation binária) | `Keys_and_scopes_are_compared_ordinally` (documenta o contrato; passava antes). **Dependência:** `TEC.ORM/TEC.ORM.SqlServer/Idempotency/IdempotencyRecord.cs` precisa de `UseCollation("Latin1_General_100_BIN2")` em `Scope` e `IdempotencyKey` (+ migração) — não editado aqui (repositório irmão) |
| B-05 | ✅ Corrigido | `IIdempotencyStore.cs` (`IdempotencyRequest.Validate()` rejeita chave `default`); `InMemoryIdempotencyStore.cs` (`CompleteAsync`/`AbandonAsync`) | `Default_key_is_rejected_by_the_store` |
| B-06 | ✅ Corrigido (com `.AddAspNetCore()`) | `TEC.Cqrs/Internal/ValidatorStartupCheck.cs` (novo); `DependencyInjection/ServiceCollectionExtensions.cs`; `TEC.Cqrs.AspNetCore/Internal/CqrsStartupValidationService.cs` (novo, `IHostedService`); `CqrsBuilderAspNetCoreExtensions.cs`; `CqrsOptions.cs` (doc) | `Command_without_validator_fails_at_startup`, `Startup_check_accepts_validators_registered_after_AddTecCqrs`, `Startup_check_is_skipped_when_requirement_is_disabled`. Fora do ASP.NET Core a verificação continua só em execução: o núcleo não referencia `Microsoft.Extensions.Hosting.Abstractions`, e adicionar o pacote exigiria regenerar os `packages.lock.json`, o que depende do feed `tec-interno` (401 nesta máquina) |
| B-07 | ✅ Corrigido | `IdempotencyMiddleware.cs` | `Header_with_several_values_and_empty_first_is_invalid` |
| B-08 | ✅ Corrigido | `Directory.Build.props` (`0.2.0`); `CHANGELOG.md` (`## [Não publicado]`) | — |
| I-01 | ⏸️ Não corrigido | — | O cabeçalho `WWW-Authenticate` depende do esquema de autenticação da aplicação; chamar `ChallengeAsync()` trocaria o corpo `ApiResponse` pelo do handler do esquema e mudaria o contrato das respostas 401. Não é trivial nem seguro sem decisão de design |
| I-02 | 🟡 Parcial | `IdempotencyMiddleware.cs` (buffer do hash do `ArrayPool`; resposta capturada repassada ao store sem `ToArray()`) | — A cópia defensiva do `InMemoryIdempotencyStore` foi mantida (protege o store de alterações no buffer do chamador, coberta por `Stored_response_is_a_defensive_copy`) |
| I-03 | ✅ Corrigido | Testes acima (25 novos) | Os testes `net8.0` continuam exigindo o runtime ASP.NET Core 8 no agente |
| I-04 | ⏸️ Não corrigido | — | Trocar versões exige regenerar lock files com o feed `tec-interno` (401 nesta máquina) e confirmar a publicação do TEC.Core 0.1.0; fica para quem tem acesso ao feed |

### Resultado de build e testes

| Etapa | Comando | Resultado |
|---|---|---|
| Build Release, modo local (solução inteira: pacotes, testes, testes de carga, samples) | `dotnet build TEC.Cqrs.slnx -c Release -p:TecUseLocalProjects=true` | ✅ **0 avisos, 0 erros** em `net8.0` e `net10.0` |
| Testes unitários `net10.0` | `dotnet test --project TEC.Cqrs.Tests -c Release --no-build` | ✅ **280/280** aprovados (eram 255; +25) |
| Testes unitários `net8.0` | idem | ⚠️ Não executam nativamente: `Microsoft.AspNetCore.App 8.0` ausente (só 10.0.12). Executados com `DOTNET_ROLL_FORWARD=LatestMajor` (binário `net8.0` sobre o runtime 10): ✅ **287/287** — não substitui a execução no runtime 8 do CI |
| Formatação | `dotnet format whitespace TEC.Cqrs.slnx --verify-no-changes` | ✅ Nenhuma pendência nos arquivos do TEC.Cqrs (as apontadas são do TEC.Core vizinho, fora do escopo) |
| Sample | `TEC.Cqrs.SampleApi` (net10.0) | ✅ Sobe com a nova verificação de validadores na subida |
| Restore em modo pacote | `dotnet build TEC.Cqrs.slnx` | ⛔ Bloqueado como antes (feed `tec-interno`, 401). Nenhum pacote novo foi adicionado; nenhum lock file foi alterado |

---

## 🔬 Auditoria de Robustez e Alinhamento com o Livro CQRS — 2026-10-23

Revisão técnica complementar e auditoria comparativa entre a biblioteca `TEC.Cqrs` e o livro de referência **CQRS na Prática: Guia Completo de Arquitetura e Implementação em .NET** (*Roberto Luiz de Oliveira*):

### 1. Robustez no Tratamento de Cancelamento no Pipeline
- **Achado:** No `ExceptionBehavior<TRequest, TResponse>`, mappers de exceção (`IExceptionErrorMapper`) registrados pela aplicação poderiam interceptar genericamente derivadas de `Exception` ou `TimeoutException` mesmo quando a requisição havia sido cancelada pelo chamador (`cancellationToken.IsCancellationRequested`).
- **Correção Aplicada:** Adicionada guarda explícita `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }` antes da conversão de `AppException` e de `_mappers.TryMapException`. Isso garante que o cancelamento do cliente propague diretamente para o `LoggingBehavior` (onde gera log Debug e marcação `cqrs.canceled = true` na `Activity`, sem poluir telemetria com falsos erros de sistema).
- **Validação:** Todos os 280 testes unitários em `net10.0` e 287 em `net8.0` continuam com 100% de aprovação.

### 2. Formalização das Decisões de Arquitetura (ADRs)
- Criada a pasta canônica `docs/adr/` com 10 Registros de Decisões de Arquitetura detalhados (ADR-0001 a ADR-0010), documentando desde o despacho pré-compilado sem reflexão até a gestão transacional com rollback-only e observabilidade nativa OpenTelemetry.

### 3. Sugestões Estratégicas de Evolução Alinhadas ao Livro CQRS
1. **Ponte de Domínio: Despacho Automático de `IDomainEvent` (`TEC.Core.Domain.AggregateRoot`):**
   - *Contexto:* O livro (Capítulo 12) aborda a coleta e o despacho de eventos gerados em entidades agregadas. O `TEC.Core` já possui a interface `IHasDomainEvents` e o agregado base `AggregateRoot<TId>`.
   - *Sugestão:* Disponibilizar no `TEC.Cqrs` ou pacote irmão (`TEC.ORM`) uma extensão para `IUnitOfWork` e interceptor de EF Core que colete automaticamente todos os `DomainEvents` de agregados rastreados e os despache via `IPublisher.Publish` (imediatos, antes do commit) ou `IPublisher.PublishAfterCommit` (após o commit), invocando em seguida `ClearDomainEvents()`.
2. **Idempotência no Nível de Command (Workers e Mensageria):**
   - *Contexto:* Atualmente a idempotência está no nível de HTTP Middleware (`TEC.Cqrs.AspNetCore`). Porém, como o livro demonstra nos Capítulos 15 e 18, comandos entram com frequência por filas de mensageria (RabbitMQ, Kafka) ou Jobs em segundo plano.
   - *Sugestão:* Criar um `IdempotencyBehavior<TRequest, TResponse>` ativado por atributo `[IdempotentCommand(KeyProperty = nameof(...))]`, permitindo que o `IIdempotencyStore` proteja comandos em qualquer canal de entrada sem depender do ASP.NET Core.
3. **Behavior de Cache de Leitura (`CachingQueryBehavior`):**
   - *Contexto:* O livro (Capítulos 13 e 21) destaca o lado da leitura (*Read Model*) e a importância de cache de consultas para redução de latência e alívio do banco.
   - *Sugestão:* Implementar behavior de cache para queries marcadas com `[CacheQuery(DurationSeconds = 60)]`, integrado a `IMemoryCache` ou `IDistributedCache`.
4. **Behavior de Auditoria Corporativa (`AuditBehavior`):**
   - *Contexto:* Em conformidade com LGPD e requisitos bancários/fiscais (Capítulo 22 do livro), comandos mutantes devem registrar quem executou, quando e o desfecho, utilizando os mascaradores de dados sensíveis do `TEC.Core` para não logar senhas ou dados confidenciais.

---
⬅️ [💻 Desenvolvimento local](desenvolvimento.md) · [📚 Índice](README.md)
