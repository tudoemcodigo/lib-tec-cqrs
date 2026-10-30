[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0001

# ADR-0001: Mediator In-Process com Despacho Pré-Compilado e Zero Reflexão no Caminho Quente

## Status
`Aceito`

## Contexto e Problema
A implementação tradicional do padrão Mediator em aplicações .NET (frequentemente inspirada no MediatR inicial) apoia-se em despacho dinâmico via reflexão em tempo de execução (`Type.MakeGenericType`, invocação reflexiva de métodos via `MethodInfo.Invoke` ou compilação de árvores de expressão via DLR). 

Embora conveniente no desenvolvimento tradicional com JIT (.NET Framework / .NET Core), essa abordagem apresenta sérios entraves para arquiteturas modernas de alta performance:
1. **Incompatibilidade com Native AOT e Trimming:** A reflexão dinâmica descontrolada inviabiliza o stripping agressivo de código do compilador AOT, gerando avisos `IL2026` / `IL3050` e risco iminente de `MissingMetadataException` em produção.
2. **Degradação de Throughput:** Invocação reflexiva contínua em microsserviços de alto volume gera sobrecarga de CPU e alocação desnecessária de memória no caminho quente de cada requisição.
3. **Falta de Segregação de Interfaces (ISP):** Clientes frequentemente precisam apenas enviar comandos ou consultas (`ISender`), mas são forçados a acoplar-se a publicadores de eventos (`IPublisher`), dificultando testes unitários e violando o princípio de menor privilégio.

O livro **CQRS na Prática** (*Capítulo 06: O padrão Mediator e o despacho*) aborda explicitamente essa evolução: da abordagem ingênua com reflexão até a resolução via wrappers pré-compilados e montados no registro do container.

## Decisão
Adotou-se o padrão Mediator in-process arquitetado com três decisões nucleares:

1. **Segregação de Interfaces (`ISender`, `IPublisher`, `IMediator`):**
   - `ISender`: Contrato estrito para despacho 1-para-1 de `IRequest<TResponse>`.
   - `IPublisher`: Contrato para publicação 1-para-N de `INotification` (tanto imediata quanto pós-commit).
   - `IMediator`: Composição que unifica ambos quando o ponto de entrada necessita dos dois comportamentos.
2. **Wrappers Pré-Compilados e CqrsRegistry:**
   - Durante a inicialização (`AddTecCqrs`), as instâncias especializadas de `RequestHandlerWrapper<TResponse>` e `NotificationTarget` são criadas e armazenadas no registro imutável `CqrsRegistry`.
   - O `Mediator.Send` consulta o `CqrsRegistry` pelo tipo em tempo de execução (busca em `FrozenDictionary` montado no registro) e invoca diretamente o wrapper tipado `RequestHandlerWrapperImpl<TRequest, TResponse>.Handle`, eliminando qualquer chamada reflexiva no caminho de despacho.
3. **Resolução de Handlers em Tempo de Vida Scoped:**
   - Handlers de requisição e notificação são sempre registrados como `Scoped`. Resoluções do mediator criam o pipeline isolado por escopo de requisição, garantindo isolamento de estado e contexto de transação.

## Consequências

### Positivas
- **Compatibilidade com Native AOT:** o despacho dos handlers registrados pelo `AddTecCqrs` não usa `MakeGenericType` nem reflexão por chamada. A única exceção fora do registro explícito são os handlers registrados direto no container (só em JIT, pelo `CqrsRegistry.CreateDynamicHandler`, protegido por `RuntimeFeature.IsDynamicCodeSupported`). Os authorizers de classes base e interfaces que se aplicam a cada requisição conhecida são calculados no `AddTecCqrs`, sem `GetInterfaces`, `MakeGenericType` nem `Activator` em execução. A verificação combina os analisadores de AOT e trimming (avisos como erro) com o job `Native AOT` do CI, que publica o `samples/TEC.Cqrs.AotSample` com `PublishAot=true` e executa o binário.
- **Zero Overhead de Despacho:** Latência de despacho desprezível em relação à execução direta do handler de negócio.
- **Fail-Fast no Boot:** Handlers duplicados para a mesma requisição ou handlers registrados direto no container com tempo de vida diferente de `Scoped` (ex.: Singleton) geram falha imediata na inicialização do container (`InvalidOperationException`).

### Negativas / Limitações
- **Varredura por Assembly requer JIT:** A descoberta automática de handlers via `RegisterServicesFromAssembly` usa reflexão de inspeção. Em ambientes Native AOT com trimming extremo, o desenvolvedor deve utilizar o registro explícito de cada handler (`AddCommandHandler` / `AddQueryHandler`).

## Referências
- Livro *CQRS na Prática*, Capítulo 06: *O padrão Mediator e o despacho*.
- Livro *CQRS na Prática*, Capítulo 21: *Performance, Native AOT e source generators*.
- Código: `TEC.Cqrs.Abstractions.ISender`, `TEC.Cqrs.Abstractions.IMediator`, `TEC.Cqrs.Internal.Mediator`, `TEC.Cqrs.Internal.CqrsRegistry`.

---
[🏛️ Índice de ADRs](README.md) · [ADR-0002](0002-result-pattern-obrigatorio-em-todas-as-requisicoes.md) ➡️
