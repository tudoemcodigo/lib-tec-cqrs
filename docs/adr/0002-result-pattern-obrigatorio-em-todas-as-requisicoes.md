[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0002

# ADR-0002: Encapsulamento Obrigatório de Resposta em Result Pattern e Repúdio a Exceções para Fluxo

## Status
`Aceito`

## Contexto e Problema
Tradicionalmente, pipelines de mediators em .NET permitem que requisições retornem qualquer tipo `TResponse` (tipos primitivos, DTOs de negócio, `void` ou `Task`). Quando uma regra de negócio falha, uma validação é violada ou o recurso não é encontrado, o desenvolvedor é forçado a:
1. **Lançar Exceções (`throw new BusinessException(...)`):** O lançamento de exceções em .NET é extremamente custoso para a runtime (alocação de stack trace, interrupção de fluxo da CPU). Em cenários de alta concorrência com validações falhas frequentes, o throughput despenca severamente.
2. **Retornar `null`:** Introduz ambiguidade sobre o motivo da ausência de retorno e induz falhas de `NullReferenceException`.
3. **Impossibilitar a Interrupção Limpa pelo Pipeline:** Se um comportamento do pipeline (ex: autorização ou validação) identifica que a requisição não deve prosseguir, ele não possui mecanismo nativo de retornar um erro estruturado sem lançar uma exceção de interrupção.

O livro **CQRS na Prática** (*Capítulo 07: Result pattern e erros*) aborda exaustivamente os custos ocultos de exceções como fluxo de controle e demonstra a necessidade de padronizar retornos funcionais no mediator.

## Decisão
Decidiu-se que **toda e qualquer requisição processada pelo TEC.Cqrs deve obrigatoriamente retornar `Result` ou `Result<T>`** (`TEC.Core.Common.Results.Result`):

1. **Restrição Genérica nos Contratos Base:**
   ```csharp
   public interface IRequest<TResponse> : IBaseRequest where TResponse : Result;
   public interface ICommand : IRequest<Result>, ICommandBase;
   public interface ICommand<TValue> : IRequest<Result<TValue>>, ICommandBase;
   public interface IQuery<TValue> : IRequest<Result<TValue>>, IQueryBase;
   ```
2. **Rejeição de Retornos Arbitrários:**
   Nenhum comando ou query pode retornar tipos brutos sem o envelope `Result`. Requisições sem esse marcador são rejeitadas em tempo de compilação ou na inicialização do serviço.
3. **Curto-Circuito Seguro nos Behaviors:**
   Behaviors de autorização (`AuthorizationBehavior`) e validação (`ValidationBehavior`) podem interromper o processamento instantaneamente retornando `ResultFactory<TResponse>.Failure(errors)`, sem alocar exceções nem instanciar o handler de negócio.
4. **Tratamento de Exceções não Tratadas:**
   Se um handler de terceiro lançar `AppException` (do `TEC.Core`) ou exceções mapeadas por `IExceptionErrorMapper`, o `ExceptionBehavior` captura a exceção e a converte de forma transparente em `Result.Failure`, preservando a consistência do pipeline.

## Consequências

### Positivas
- **Alta Performance:** Elimina completamente o custo de alocação de stack frames para erros esperados de negócio (validação, conflito, não encontrado).
- **Tratamento Uniforme de Falhas:** Todo consumidor de `ISender.Send` sabe exatamente como verificar o resultado (`if (result.IsFailure)`), tratando erros de forma determinística e tipada.
- **Mapeamento Direto para HTTP:** Facilita a conversão em respostas REST padronizadas (`ApiResponseHttpResult`), onde o status HTTP deriva diretamente do `ErrorType` do erro contido no `Result`.

### Negativas / Limitações
- **Curva de Aprendizado:** Desenvolvedores acostumados a lançar exceções em qualquer validação precisam adotar o fluxo com retornos `Result`. No entanto, como o `ExceptionBehavior` converte `AppException` automaticamente, equipes em transição não são penalizadas.

## Referências
- Livro *CQRS na Prática*, Capítulo 07: *Result pattern e erros*.
- ADR-0001 do `TEC.Core`: *Adoção do Result Pattern em Substituição a Exceções para Controle de Fluxo*.
- Código: `TEC.Cqrs.Abstractions.IRequest`, `TEC.Cqrs.Behaviors.ValidationBehavior`, `TEC.Cqrs.Behaviors.ExceptionBehavior`.

---
⬅️ [ADR-0001](0001-mediator-in-process-e-despacho-pre-compilado.md) · [🏛️ Índice de ADRs](README.md) · [ADR-0003](0003-pipeline-estrito-de-comportamentos-e-fail-closed.md) ➡️
