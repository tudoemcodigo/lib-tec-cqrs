<div align="center">
<img src="https://www.tudoemcodigo.com/marca/PNG/tudoemcodigo-icone-512.png" alt="Tudo em Código" width="90" />
</div>

[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › 🏛️ ADRs

# 🏛️ Registros de Decisões de Arquitetura (ADRs) — TEC.Cqrs

> Este diretório documenta as decisões arquiteturais fundamentais adotadas **exclusivamente no TEC.Cqrs**, fundamentadas nos conceitos e práticas detalhados no livro de referência **CQRS na Prática: Guia Completo de Arquitetura e Implementação em .NET** (*Roberto Luiz de Oliveira*), contextualizando requisitos de alta engenharia, resiliência, segurança por padrão e compatibilidade estrita com Native AOT.

## 📑 Índice de Decisões

| ADR | Título | Status | Área / Módulo |
|:---:|---|:---:|---|
| [ADR-0001](0001-mediator-in-process-e-despacho-pre-compilado.md) | Mediator In-Process com Despacho Pré-Compilado e Zero Reflexão no Caminho Quente | `Aceito` | `Abstractions / Dispatch` |
| [ADR-0002](0002-result-pattern-obrigatorio-em-todas-as-requisicoes.md) | Encapsulamento Obrigatório de Resposta em Result Pattern e Repúdio a Exceções para Fluxo | `Aceito` | `Abstractions / Results` |
| [ADR-0003](0003-pipeline-estrito-de-comportamentos-e-fail-closed.md) | Pipeline Estrito de Behaviors, Ordem Determinística e Defesa em Profundidade | `Aceito` | `Behaviors / Pipeline` |
| [ADR-0004](0004-autorizacao-em-tres-camadas-e-controle-granular-de-permissoes.md) | Autorização Declarativa em Três Níveis (Atributo, Permissão e Autorizador) com Fail-Closed | `Aceito` | `Authorization` |
| [ADR-0005](0005-validacao-obrigatoria-de-comandos-e-integracao-fluentvalidation.md) | Validação Obrigatória de Comandos na Inicialização e Integração Polimórfica com FluentValidation | `Aceito` | `Validation` |
| [ADR-0006](0006-gestao-transacional-com-comandos-aninhados-e-rollback-only.md) | Gestão Transacional Automática com Suporte a Comandos Aninhados e Marcação Rollback-Only | `Aceito` | `Persistence / Transaction` |
| [ADR-0007](0007-notificacoes-imediatas-atomicas-vs-pos-commit-isoladas.md) | Notificações Imediatas Atômicas vs Pós-Commit com Garantia de Isolamento e Resiliência | `Aceito` | `Notifications` |
| [ADR-0008](0008-idempotencia-http-com-reserva-concorrente-e-hash-criptografico.md) | Idempotência HTTP por Chave de Cabeçalho com Reserva Concorrente, Escopo Criptográfico e Tolerância a Desconexões | `Aceito` | `AspNetCore / Idempotency` |
| [ADR-0009](0009-mapeamento-seguro-de-respostas-http-e-mascaramento-de-erros.md) | Mapeamento Seguro de Respostas HTTP, Sanitização de Erros 500/502 e Proteção de Cabeçalho Location | `Aceito` | `AspNetCore / Results` |
| [ADR-0010](0010-observabilidade-nativa-com-opentelemetry-e-metricas-dimensionadas.md) | Observabilidade Integrada com OpenTelemetry Nativo, Métricas Sem Cardinalidade Descontrolada e Traces Sanitizados | `Aceito` | `Diagnostics / Metrics` |
| [ADR-0011](0011-alvo-unico-net10-e-verificacao-native-aot-por-publicacao.md) | Alvo Único .NET 10 e Verificação de Native AOT por Publicação Real | `Aceito` | `Build / AOT` |

---

## 🧭 Pilares Arquiteturais do TEC.Cqrs

Toda a arquitetura do **TEC.Cqrs** está alinhada ao livro **CQRS na Prática** e foi projetada sob quatro pilares inegociáveis:

1. **Segurança por Padrão (*Fail Closed*):** Nenhuma requisição entra no pipeline desprotegida. Se a autorização não for explicitamente declarada (`RequireAuthorization`), a aplicação falha na inicialização. Comandos sem validador (`RequireValidatorForCommands`) são barrados na subida do host quando a aplicação usa `.AddAspNetCore()`; sem ele, na primeira execução. Erros internos do servidor nunca vazam para o cliente HTTP.
2. **Alta Performance e Native AOT:** O caminho quente de execução (handlers, wrappers de despacho e resolução de behaviors) não utiliza reflexão (`System.Reflection`), nem emite código dinâmico (`Reflection.Emit`), com as exceções descritas no [ADR-0001](0001-mediator-in-process-e-despacho-pre-compilado.md). Os pacotes são compilados com os analisadores de AOT e trimming como erro e o registro explícito dos handlers é o caminho para Native AOT (.NET 10); o job `Native AOT` do CI publica o `samples/TEC.Cqrs.AotSample` com `PublishAot` e executa o binário.
3. **Consistência e Atomicidade Concorrente:** Transações são acopladas ao ciclo de vida do Command (`TransactionBehavior`). Comandos aninhados participam da transação externa e qualquer falha interna condena atomicamente a transação como *rollback-only*, impedindo persistências parciais ou corrupção de estado. Notificações pós-commit só são despachadas se o commit for completado com sucesso irrestrito.
4. **Idempotência Real e Tolerância a Falhas de Rede:** Operações críticas são protegidas contra execuções duplicadas causadas por timeouts de cliente, retries de gateways ou perdas de conexão antes da entrega da resposta, utilizando reserva em duas fases (*Lock/Complete*) e hash de payload SHA-256.

---

## 📐 Template dos ADRs

Todos os ADRs seguem a mesma estrutura:

| Seção | Conteúdo |
|---|---|
| `## Status` | `Aceito` (ou `Proposto`, `Substituído por ADR-NNNN`) |
| `## Contexto e Problema` | O problema e as forças em jogo, com a referência ao livro |
| `## Decisão` | O que foi decidido e como está implementado |
| `## Consequências` | `### Positivas` e `### Negativas / Limitações` |
| `## Referências` | Livro, normas e os tipos do código envolvidos |

---
⬅️ [📚 Índice da documentação](../README.md) · [🏠 README](../../README.md) · [ADR-0001](0001-mediator-in-process-e-despacho-pre-compilado.md) ➡️
