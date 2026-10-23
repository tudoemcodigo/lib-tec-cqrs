[🏛️ TEC.Cqrs](../../README.md) ➔ [📖 Documentação](../README.md) ➔ [ADRs](README.md) ➔ ADR-0006

# ADR-0006: Gestão Transacional Automática com Suporte a Comandos Aninhados e Marcação Rollback-Only

## Status
`Aceito`

## Contexto e Problema
A integridade de escrita em aplicações corporativas depende do controle estrito de transações e unidades de trabalho (*Unit of Work*). Cenários comuns geram anomalias catastróficas:
1. **Gravação Parcial por Comandos Aninhados:** Quando o comando A despacha internamente o comando B via `ISender.Send`, se o comando B abrir e confirmar sua própria transação e o comando A falhar posteriormente, os dados de B permanecem gravados, corrompendo a consistência do banco de dados.
2. **Engolimento Silencioso de Erros pelo Comando Externo:** Se o comando B falhar e o comando A capturar a falha em um bloco `try/catch` ou tratar o `Result` retornando sucesso, a transação externa confirmaria metade da operação.
3. **Concorrência Incorreta de DbContext:** `DbContext` do Entity Framework Core não é thread-safe. Se o handler de um comando disparar comandos internos em paralelo via `Task.WhenAll`, ocorre concorrência destrutiva na mesma conexão e no mesmo `IUnitOfWork`.
4. **Cancelamento Indevido de Commit/Rollback:** Se o token de cancelamento da requisição HTTP for repassado diretamente para `CommitAsync` ou `RollbackAsync`, a desconexão do cliente durante o commit pode abortar o processo na metade, deixando o banco em estado incerto.

O livro **CQRS na Prática** (*Capítulo 11: Transações e Unit of Work*) dedica uma análise aprofundada sobre como resolver o desafio de transações aninhadas e concorrência no escopo.

## Decisão
Criou-se uma arquitetura transacional robusta no `TransactionBehavior`, coordenada pelo `PipelineContext` e `AsyncLocal<RequestFrame>`:

1. **Escopo Transacional Exclusivo para Commands:**
   - Queries (`IQueryBase`) e comandos marcados com `[SkipTransaction]` nunca abrem transações.
2. **Participação de Comandos Aninhados na Mesma Transação:**
   - O comando mais externo abre a transação no `IUnitOfWork` (`BeginTransactionAsync`).
   - Comandos internos detectam a transação ativa do frame pai e participam dela, sem abrir uma segunda transação.
3. **Mecanismo Atômico Rollback-Only:**
   - Se um comando interno falhar (retornar `Result.Failure` ou lançar exceção), a transação compartilhada é marcada irrevogavelmente como `Rollback-Only` (`transaction.MarkRollbackOnly(...)`).
   - Mesmo que o comando externo intercepte a falha e tente retornar sucesso, o `TransactionBehavior` bloqueia o commit, efetua o rollback completo e retorna erro `TRANSACAO_DESFEITA` acompanhado dos erros originais do comando interno.
4. **Proteção Contra Concorrência no Escopo:**
   - O `PipelineContext` impede múltiplos `Send` simultâneos na mesma transação. Se `Task.WhenAll` for utilizado para comandos internos, o mediator lança `InvalidOperationException` imediatamente para impedir a corrupção do `DbContext`.
5. **Commit e Rollback Resilientes com `CancellationToken.None`:**
   - Tanto `unitOfWork.CommitAsync(CancellationToken.None)` quanto `unitOfWork.RollbackAsync(CancellationToken.None)` ignoram o cancelamento da requisição externa: uma transação que iniciou seu encerramento deve ser concluída deterministicamente, independentemente da queda do socket de rede.

## Consequências

### Positivas
- **Garantia ACID Absoluta:** Impossível ocorrer gravação pela metade ou confirmação de estado corrompido em cenários aninhados.
- **Prevenção de Corrupção Concorrente:** Proteção ativa contra uso indevido de chamadas paralelas em instâncias com `DbContext`.
- **Desacoplamento de ORM:** O `TEC.Cqrs` interage unicamente com a abstração `IUnitOfWork`, funcionando de forma idêntica com EF Core, Dapper ou ADO.NET puro.

### Negativas / Limitações
- **Operações Longas:** Comandos complexos com múltiplos comandos internos acumulam trabalho dentro da mesma transação de banco. Recomenda-se manter comandos enxutos e usar consistência eventual (Sagas / Outbox) para fluxos distribuídos.

## Referências
- Livro *CQRS na Prática*, Capítulo 11: *Transações e Unit of Work*.
- Código: `TEC.Cqrs.Persistence.IUnitOfWork`, `TEC.Cqrs.Behaviors.TransactionBehavior`, `TEC.Cqrs.Internal.PipelineContext`.
