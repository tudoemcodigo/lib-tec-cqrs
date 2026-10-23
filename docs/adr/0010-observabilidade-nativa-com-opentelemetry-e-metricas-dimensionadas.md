[🏛️ TEC.Cqrs](../../README.md) ➔ [📖 Documentação](../README.md) ➔ [ADRs](README.md) ➔ ADR-0010

# ADR-0010: Observabilidade Integrada com OpenTelemetry Nativo, Métricas Sem Cardinalidade Descontrolada e Traces Sanitizados

## Status
`Aceito`

## Contexto e Problema
A observabilidade em microsserviços modernos é essencial para diagnóstico em produção, SLA de operações e alertas de incidentes. No entanto, implementações ingênuas de rastreamento e métricas em mediators costumam trazer riscos graves:
1. **Explosão de Cardinalidade (*High Cardinality*):** Gravar IDs de clientes, CPFs, códigos de pedido ou payloads como tags/dimensões de métricas (`Meter`) exaure a memória do Prometheus/Datadog e degrada drasticamente a infraestrutura de monitoramento.
2. **Vazamento de Dados Sensíveis em Traces (LGPD):** Anexar parâmetros brutos do command (senhas, documentos, cartões de crédito) aos spans da `Activity` expõe dados confidenciais a sistemas de rastreamento distribuído (Jaeger, Zipkin, Dynatrace), violando a legislação de privacidade.
3. **Dependência de Pacotes Pesados:** Acoplar a biblioteca do mediator a pacotes de terceiros de APM em vez de utilizar as APIs nativas do .NET (`System.Diagnostics.ActivitySource` e `System.Diagnostics.Metrics`).
4. **Poluição de Logs com Exceções Duplicadas:** Uma única exceção ser logada 3 ou 4 vezes (pelo handler, pelo behavior de log, pelo controller e pelo middleware global de exceções).

O livro **CQRS na Prática** (*Capítulo 19: Observabilidade* e *Capítulo 22: Segurança em aplicações CQRS*) aborda a arquitetura de observabilidade canônica em aplicações CQRS.

## Decisão
Implementou-se instrumentação nativa zero-dependency baseada nas APIs da BCL do .NET e nos padrões semânticos do OpenTelemetry:

1. **ActivitySource Nativo (`TEC.Cqrs`):**
   - Cada requisição gera um span (`Activity`) iniciado no `LoggingBehavior`.
   - Tags estruturadas padronizadas:
     - `cqrs.request`: Nome completo do tipo da requisição (ex.: `CriarClienteCommand`).
     - `cqrs.kind`: Identificador de categoria (`command` ou `query`).
     - `cqrs.success`: Booleano indicando o desfecho (`true`/`false`).
     - `cqrs.outcome`: Categoria do desfecho (`success`, `failure`, `exception`, `canceled`).
     - `cqrs.error_code`: Código padronizado de erro do `Result` quando houver falha.
   - **Sanitização de Payloads:** Parâmetros da requisição e dados do payload **nunca** são adicionados aos spans ou logs.
   - **Controle de Detalhes de Exceção:** Mensagem e stack trace só são anexados ao span se `CqrsOptions.RecordExceptionDetailsInTraces = true`. O log sempre recebe a exceção completa, mas o span de APM permanece seguro por padrão.
2. **Métricas de Baixa Cardinalidade (`CqrsMetrics`):**
   - Utiliza `System.Diagnostics.Metrics.Meter` com nome `"TEC.Cqrs"`.
   - Métrica principal: `cqrs.requests.duration` (Histograma em segundos).
   - Dimensões estritamente controladas: apenas `request_name`, `request_kind`, `outcome` e `error_type` (ex.: `Validation`, `Conflict`). Nenhuma informação de entidade ou usuário entra nas métricas, garantindo estabilidade e custo zero de memória nos agregadores.
3. **Supressão Ativa de Logs Redundantes (`LoggedExceptions`):**
   - Uma exceção não tratada é marcada internamente (`LoggedExceptions.TryMark(ex)`).
   - Se já registrada pelo `LoggingBehavior`, o middleware global de exceção (`TecExceptionHandlerMiddleware`) suprime a gravação redundante, mantendo os logs limpos e fáceis de analisar em incidentes.
4. **Telemetria de Notificações (`NotificationTelemetry`):**
   - Spans específicos para publicação de eventos (`NotificationPublisher`), com atributo `cqrs.notification` e marcação de execução pós-commit (`after_commit = true`).

## Consequências

### Positivas
- **Padrão W3C TraceContext Nativo:** Rastreamento ponta a ponta correlacionando requisições HTTP, despacho interno do mediator e chamadas a bancos ou mensageria.
- **Zero Vazamento de Dados Pessoais:** Conformidade total com a LGPD e políticas corporativas de segurança.
- **Eficiência e Estabilidade em Alta Escala:** Zero risco de sobrecarga de coletores de métricas.

### Negativas / Limitações
- **Diagnóstico sem Payload:** Para inspecionar o payload exato que provocou uma falha, o desenvolvedor deve recorrer a logs específicos de auditoria ou reprodução controlada em ambiente de homologação.

## Referências
- Livro *CQRS na Prática*, Capítulo 19: *Observabilidade*.
- Livro *CQRS na Prática*, Capítulo 22: *Segurança em aplicações CQRS*.
- OpenTelemetry Semantic Conventions for General Attributes and Messaging.
- Código: `TEC.Cqrs.Diagnostics.CqrsDiagnostics`, `TEC.Cqrs.Internal.CqrsMetrics`, `TEC.Cqrs.Behaviors.LoggingBehavior`.
