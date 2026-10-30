[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0008

# ADR-0008: Idempotência HTTP por Chave de Cabeçalho com Reserva Concorrente, Escopo Criptográfico e Tolerância a Desconexões

## Status
`Aceito`

## Contexto e Problema
Operações mutantes críticas (ex.: pagamentos, estornos, emissão de faturamento) sofrem com falhas inerentes de redes não confiáveis:
1. **Replay Acidental e Duplicidade de Cobrança:** Se o cliente envia uma requisição de pagamento, a operação é confirmada no banco, mas a rede cai antes da resposta chegar, o cliente ou o frontend reenvia a requisição. Sem proteção, a operação seria executada duas vezes.
2. **Corrida Concorrente (*Race Condition*):** Duas requisições paralelas enviando a mesma chave ao mesmo tempo podem executar em paralelo se o sistema apenas verificar a existência prévia sem efetuar bloqueio ou reserva atômica.
3. **Replay Cruzado entre Tenants ou Usuários (Vulnerabilidade M-02):** Se a chave for global (`"123"`), um usuário pode bloquear ou reutilizar a chave de outro usuário ou tenant, recebendo dados privados de outra organização.
4. **Desconexão Após o Commit Liberando a Chave (Vulnerabilidade A-01):** Quando a resposta é serializada diretamente no socket e o cliente fecha a conexão, a escrita lança `OperationCanceledException`. Se o middleware liberar a chave no `catch`, a repetição subsequente executará a operação novamente no banco, aniquilando a idempotência.

O livro **CQRS na Prática** (*Capítulo 15: Outbox, inbox, mensageria e idempotência* e *Capítulo 22: Segurança em aplicações CQRS*) discute a especificação IETF para `Idempotency-Key` e a mitigação rigorosa de replays.

## Decisão
Criou-se uma arquitetura completa de idempotência no pacote `TEC.Cqrs.AspNetCore` via `IdempotencyMiddleware`, complementada por `IIdempotencyStore` e `InMemoryIdempotencyStore`:

1. **Ciclo de Vida em Duas Fases (*TryBegin* e *Complete/Abandon*):**
   - Ao receber o cabeçalho `Idempotency-Key`, o middleware tenta reservar a chave (`store.TryBeginAsync`).
   - Se já completada: retorna imediatamente a resposta guardada (com cabeçalho `Idempotent-Replayed: true`).
   - Se em andamento: retorna HTTP 409 Conflict (`IDEMPOTENCIA_EM_ANDAMENTO`).
   - Se o payload divergir da primeira tentativa (mesma chave com outro corpo): retorna HTTP 422 Unprocessable Entity (`IDEMPOTENCY_KEY_REUTILIZADA`).
   - Se nova: obtém um `lockId` temporário com prazo de validade (`LockTimeout`).
2. **Escopo Criptográfico Blindado com SHA-256 (Mitigação M-02):**
   - O escopo não utiliza o ID bruto do usuário. O escopo é calculado como o hash SHA-256 combinando:
     - Tipo do Claim (ex.: `tec_uid`, `oid`, `sub`),
     - Emissor do Claim (`Issuer`),
     - Valor do Claim autenticado,
     - `ScopeDiscriminator` opcional (ex.: `tenant_id`).
   - Isso isola as chaves por usuário, por esquema de autenticação e, com o `ScopeDiscriminator`, por tenant: um usuário não acessa respostas guardadas de outro.
3. **Tolerância a Desconexões do Cliente Após o Commit (Mitigação A-01):**
   - O corpo da resposta de endpoints idempotentes é retido em buffer seguro (`CaptureStream`) até o limite configurado (`MaxResponseBodyBytes`).
   - A resposta HTTP 2xx é confirmada e gravada no store (`CompleteAsync`) **antes** do envio ao cliente.
   - O envio para a rede é executado com `CancellationToken.None`. Se o cliente tiver desconectado, o servidor encerra sem lançar erro, e a chave permanece consolidada no store. Em caso de repetição, o cliente recebe a resposta original guardada.
   - Cancelamento ou exceção durante a execução: a reserva só é mantida se a operação teve efeito (transação do pipeline confirmada ou resposta 2xx já escrita). O status 2xx sozinho não conta, porque o `Response.StatusCode` começa em 200.
   - Falha do store ao guardar: até 3 tentativas com esperas curtas; esgotadas, a resposta vai ao cliente, a reserva fica até o `LockTimeout` e um erro estruturado é registrado sem chave nem corpo.
4. **Armazenamento em Memória Otimizado (`InMemoryIdempotencyStore`):**
   - Thread-safe, com teto global (`MaxEntries`), teto estrito por escopo/usuário (`MaxEntriesPerScope`, mitigação B-02) e orçamento global em bytes (`MaxTotalBytes`).
   - Despejo: entradas expiradas de qualquer escopo e, se preciso, só respostas do próprio escopo de quem pede. Respostas válidas de outros escopos nunca são descartadas; sem espaço, o store recusa com `InvalidOperationException` (fail closed). Entre a disponibilidade de novas chaves e a garantia de não executar duas vezes a operação de outro usuário, a decisão fica com a segunda.

## Consequências

### Positivas
- **Sem execução dupla na repetição pelo cliente:** uma repetição depois de queda de rede ou timeout recebe a resposta guardada, desde que a resposta 2xx tenha sido guardada ou a reserva continue vigente. Fora desse caso (resposta não 2xx sem transação confirmada, falha do store em `CompleteAsync` seguida de `LockTimeout`, reserva expirada), a operação pode executar de novo.
- **Isolamento por usuário:** a chave vale por usuário (tipo, emissor e valor do claim). Entre tenants, o isolamento exige que o tenant faça parte do usuário ou seja informado em `ScopeDiscriminator`; sem isso, o mesmo usuário com a mesma chave em dois tenants recebe a resposta guardada do primeiro.
- **Alinhamento ao Rascunho IETF:** Segue o cabeçalho `Idempotency-Key` descrito no rascunho *The Idempotency-Key HTTP Header Field*.

### Negativas / Limitações
- **Memória de Buffer:** Respostas armazenadas temporariamente em memória utilizam até `MaxResponseBodyBytes` (padrão 1 MiB). Respostas acima desse limite sofrem bypass de armazenamento e emitem alerta.
- **Múltiplas Instâncias:** O `InMemoryIdempotencyStore` destina-se a desenvolvimento e nós únicos; clusters com múltiplas instâncias requerem armazenamento compartilhado (ex.: o `EfIdempotencyStore` do `TEC.ORM.EntityFrameworkCore` ou um `IIdempotencyStore` próprio, como um store em Redis).

## Referências
- Livro *CQRS na Prática*, Capítulo 15: *Outbox, inbox, mensageria e idempotência*.
- Livro *CQRS na Prática*, Capítulo 22: *Segurança em aplicações CQRS*.
- [Revisão Técnica TEC.Cqrs](../revisao-tecnica.md) (2026-10-19): Achados A-01, M-02, M-03, B-02, B-04, B-05, B-07.
- IETF Draft: *The Idempotency-Key HTTP Header Field*.
- Código: `TEC.Cqrs.Idempotency.*`, `TEC.Cqrs.AspNetCore.Idempotency.*`.

---
⬅️ [ADR-0007](0007-notificacoes-imediatas-atomicas-vs-pos-commit-isoladas.md) · [🏛️ Índice de ADRs](README.md) · [ADR-0009](0009-mapeamento-seguro-de-respostas-http-e-mascaramento-de-erros.md) ➡️
