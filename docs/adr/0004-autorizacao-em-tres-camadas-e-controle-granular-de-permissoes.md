[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0004

# ADR-0004: Autorização Declarativa em Três Níveis (Atributo, Permissão e Autorizador) com Fail-Closed

## Status
`Aceito`

## Contexto e Problema
O controle de acesso em aplicações com arquitetura CQRS costuma sofrer de dois problemas graves:
1. **Acoplamento Exclusivo ao Endpoint HTTP:** Proteger apenas os endpoints ASP.NET Core (`[Authorize]`) deixa o sistema vulnerável quando o mesmo Command/Query é acionado por background jobs, filas de mensageria ou testes de integração. A regra de segurança deve morar na fronteira do caso de uso, no próprio Mediator.
2. **Autorização Rústica Baseada Apenas em Roles:** O modelo de papéis (*roles*) é insuficiente para sistemas corporativos modernos, que exigem permissões granulares (*claims-based permissions*) e verificação contextual de recursos (ex.: "o usuário só pode cancelar o pedido se o pedido pertencer à sua filial", BOLA/IDOR).
3. **Comportamento *Fail-Open* Acidental:** Se um desenvolvedor esquece de declarar proteção em um novo comando, a requisição passa livremente sem autenticação.

O livro **CQRS na Prática** (*Capítulo 10: Autorização* e *Capítulo 22: Segurança em aplicações CQRS*) detalha a defesa em profundidade e a prevenção ativa de BOLA/BFLA com autorização contextual.

## Decisão
Implementou-se um modelo de autorização em **três níveis complementares**, operando com **Fail-Closed obrigatório por padrão**:

1. **Fail-Closed por Padrão (`CqrsOptions.RequireAuthorization = true`):**
   - Toda requisição registrada deve declarar explicitamente como é autorizada.
   - Requisições sem `[AuthorizeRequest]`, `[RequirePermission]`, `IRequestAuthorizer<T>` ou o escape deliberado `[AllowAnonymousRequest]` causam falha imediata na inicialização da aplicação (`EnsureAuthorizationIsDeclared`).
2. **Três Camadas de Avaliação no `AuthorizationBehavior`:**
   - **Nível 1: Atributo Declarativo (`[AuthorizeRequest]`):** Avalia autenticação, papéis exigidos (`Roles = "Admin,Gerente"`) e políticas nomeadas (`Policy = "ExigeCertificado"` via `IRequestPolicyEvaluator`).
   - **Nível 2: Permissões Granulares (`[RequirePermission]`):** Avalia permissões pontuais via `IPermissionChecker` (claim `tec_perm` por padrão, ou uma implementação própria registrada depois do `AddTecCqrs`). Cada atributo é uma regra e `Mode` define a combinação: `PermissionMatch.All` (todas as permissões, padrão) ou `PermissionMatch.Any` (ao menos uma).
   - **Nível 3: Autorizador Contextual Baseado em Dados (`IRequestAuthorizer<TRequest>`):** Executado depois dos atributos e antes da validação de entrada. Permite que classes de autorização acessem o payload da requisição e repositórios para impedir ataques BOLA/IDOR (ex.: validar posse do recurso). Sem atributo, o authorizer sozinho já conta como autorização declarada e exige usuário autenticado (`AuthorizersRequireAuthentication`, padrão `true`); authorizers de tipo base ou interface valem para todas as requisições que os implementam.
3. **Isolamento de Identidades Autenticadas:**
   - Permissões e papéis são extraídos exclusivamente de instâncias de `ClaimsIdentity` onde `IsAuthenticated == true`. Identidades não autenticadas agregadas ao `ClaimsPrincipal` são explicitamente ignoradas.

## Consequências

### Positivas
- **Imunidade a BFLA/BOLA:** Acesso negado antes de qualquer processamento de dados.
- **Portabilidade de Ponto de Entrada:** Comandos protegidos pelo mediator continuam seguros quando invocados por Web APIs, mensageria (RabbitMQ, Kafka) ou Jobs.
- **Auditoria Transparente:** Falhas de autorização geram resultados 401 (`NAO_AUTENTICADO`) ou 403 (`ACESSO_NEGADO`) padronizados sem exceções.

### Negativas / Limitações
- **Disciplina de Marcação:** Comandos públicos deliberados (ex.: `CriarContaCommand`, `SolicitarRecuperacaoSenhaCommand`) exigem a presença explícita do atributo `[AllowAnonymousRequest]`.

## Referências
- Livro *CQRS na Prática*, Capítulo 10: *Autorização*.
- Livro *CQRS na Prática*, Capítulo 22: *Segurança em aplicações CQRS*.
- OWASP API Security Top 10 (2023): API1 (BOLA) e API5 (BFLA).
- Código: `TEC.Cqrs.Authorization.*`, `TEC.Cqrs.Behaviors.AuthorizationBehavior`.

---
⬅️ [ADR-0003](0003-pipeline-estrito-de-comportamentos-e-fail-closed.md) · [🏛️ Índice de ADRs](README.md) · [ADR-0005](0005-validacao-obrigatoria-de-comandos-e-integracao-fluentvalidation.md) ➡️
