[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0007

# ADR-0007: Notificações Imediatas Atômicas vs Pós-Commit com Garantia de Isolamento e Resiliência

## Status
`Aceito`

## Contexto e Problema
A publicação de notificações e eventos in-process (`INotification`) apresenta uma armadilha clássica de arquitetura:
1. **Disparo Imaturo de Efeitos Colaterais Externos:** Se um handler de evento envia um e-mail, notifica um webhook ou emite uma mensagem para um broker de mensageria durante o processamento do comando, e a transação de banco de dados sofre rollback logo em seguida, o efeito colateral externo não pode ser desfeito (anúncio de dados inexistentes no banco).
2. **Efeitos Atômicos Inviabilizados:** Por outro lado, determinados handlers precisam rodar **dentro** da transação (ex.: gravar um registro na tabela de Outbox na mesma transação atômica do agregado).
3. **Efeito Dominó em Handlers Pós-Commit:** Se um handler pós-commit falhar (ex.: timeout ao chamar serviço externo), essa falha não pode cancelar a transação (que já foi comitada no banco) nem derrubar a aplicação sem avisar o chamador.
4. **Loops Infinitos de Notificações:** Handlers que enfileiram novas notificações pós-commit podem gerar recursão infinita se houver ciclo de dependência.

O livro **CQRS na Prática** (*Capítulo 12: Notificações e eventos de domínio*) analisa a fundo as diferenças conceituais entre `Publish` e `PublishAfterCommit` e o isolamento de handlers.

## Decisão
A interface `IPublisher` separa formalmente duas modalidades de publicação com garantias comportamentais distintas:

1. **`IPublisher.Publish` (Publicação Imediata / Síncrona na Transação):**
   - Executa imediatamente, em sequência, chamando todos os `INotificationHandler<TNotification>`.
   - Se disparada dentro de um comando, executa **dentro da transação**. Qualquer exceção lançada interrompe a execução e desfaz a transação (*rollback*).
   - Uso recomendado: gravação atômica em Outbox, mutações no mesmo `DbContext`.
2. **`IPublisher.PublishAfterCommit` (Publicação Adiada Pós-Commit):**
   - As notificações são acumuladas na fila do `RequestFrame` atual do `PipelineContext`.
   - Se o comando falhar ou a transação sofrer rollback, a fila inteira é sumariamente descartada.
   - Somente após a confirmação bem-sucedida da transação (`Committed == true`), o `Mediator` despacha as notificações acumuladas.
3. **Isolamento de Falhas e Tolerância (*Fail-Safe* Pós-Commit):**
   - Na publicação pós-commit (`PublishIsolatedAsync`), cada handler é executado isoladamente. Falhas individuais são registradas em log com nível de erro (`DeferredNotificationFailed`, evento 1008; falha ao criar os handlers: evento 1009) e telemetria, **sem interromper** os demais handlers e **sem alterar** o resultado já confirmado retornado ao chamador.
4. **Proteção Contra Recursão Excessiva (`MaxDeferredRounds`):**
   - Se um handler pós-commit enfileirar novas notificações pós-commit, o mediator processa em rodadas sucessivas até o limite de segurança de 10 rodadas (`MaxDeferredRounds = 10`). Ultrapassado o teto, o laço é interrompido com log de erro (`DeferredNotificationRoundsExceeded`, evento 1016) e as notificações restantes são descartadas, prevenindo laço infinito.
5. **Deduplicação Inteligente por Hierarquia:**
   - Handlers registrados para interfaces ou classes base (`INotificationHandler<IEventoBase>`) são executados em ordem determinística (tipo concreto -> bases -> interfaces), executando exatamente uma vez por publicação para cada classe de handler.

## Consequências

### Positivas
- **Fim dos Efeitos Colaterais Fantasmas:** E-mails e integrações externas só disparam após o commit consolidado.
- **Resiliência Máxima:** Falha em um consumidor desacoplado não afeta a confirmação da transação principal.
- **Suporte Nativo a Outbox:** Permite orquestrar com clareza o que é atômico no banco versus o que é comunicação pós-efetivação.

### Negativas / Limitações
- **Melhor Esforço em `PublishAfterCommit`:** Se o processo da aplicação for abruptamente finalizado (falha de energia, `kill -9`) após o commit e antes do processamento pós-commit, notificações em memória podem se perder. Para garantias estritas *at-least-once*, deve-se utilizar o padrão Outbox gravado via `Publish` imediato.

## Referências
- Livro *CQRS na Prática*, Capítulo 12: *Notificações e eventos de domínio*.
- Livro *CQRS na Prática*, Capítulo 15: *Outbox, inbox, mensageria e idempotência*.
- Código: `TEC.Cqrs.Abstractions.IPublisher`, `TEC.Cqrs.Internal.Mediator`, `TEC.Cqrs.Internal.NotificationPublisher`.

---
⬅️ [ADR-0006](0006-gestao-transacional-com-comandos-aninhados-e-rollback-only.md) · [🏛️ Índice de ADRs](README.md) · [ADR-0008](0008-idempotencia-http-com-reserva-concorrente-e-hash-criptografico.md) ➡️
