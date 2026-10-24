[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0005

# ADR-0005: Validação Obrigatória de Comandos na Inicialização e Integração Polimórfica com FluentValidation

## Status
`Aceito`

## Contexto e Problema
A validação de entrada é a primeira linha de defesa contra corrupção de estado e falhas funcionais. Em projetos tradicionais:
1. **Esquecimento Humano:** Comandos novos entram em produção sem validador porque o time esqueceu de criar ou registrar o validator correspondente.
2. **Descoberta Tardia:** Se a verificação for feita apenas em tempo de execução, o primeiro usuário a submeter o comando sofrerá um erro inesperado 500 em produção.
3. **Acoplamento a Pacote Específico:** Forçar o núcleo a depender diretamente de bibliotecas externas de validação (como `FluentValidation`) acopla desnecessariamente o pacote mediator.
4. **Acúmulo de Erros Incorreto:** Execução de múltiplos validadores reutilizando instâncias de contexto pode duplicar mensagens de erro ou mascarar falhas.

O livro **CQRS na Prática** (*Capítulo 09: Validação*) aborda a diferença entre validação de comando (sintática e estrutural) e validação de domínio (invariantes de agregados), recomendando a validação obrigatória por padrão.

## Decisão
Decidiu-se pela **validação obrigatória de comandos (*Fail Closed*)**, com desacoplamento através de interface nativa e pacote satélite para FluentValidation:

1. **Abstração Neutra no Núcleo (`TEC.Cqrs`):**
   - O núcleo define apenas a interface pura `IRequestValidator<TRequest>`. Qualquer motor de validação pode implementar essa interface.
2. **Validação Obrigatória para Comandos (`CqrsOptions.RequireValidatorForCommands = true`):**
   - Todo comando (`ICommandBase`) deve possuir ao menos um validador registrado.
   - Comandos que deliberadamente não recebem parâmetros externos (ex.: `EfetivarFechamentoMesCommand`) devem ser explicitamente marcados com `[SkipValidation]`.
   - Com `.AddAspNetCore()`, a presença de validadores para todos os comandos conhecidos é **verificada na subida do host** (`CqrsStartupValidationService`), impedindo que a aplicação inicie se houver comandos desprotegidos.
3. **Pacote Satélite Especializado (`TEC.Cqrs.FluentValidation`):**
   - Fornece o adaptador `FluentValidationRequestValidator<TRequest>` que encapsula instâncias de `FluentValidation.IValidator<TRequest>`.
   - Cada validador roda com uma instância isolada de `ValidationContext<TRequest>`, garantindo que erros não sejam duplicados entre diferentes validadores.
   - Converte automaticamente as falhas (`ValidationFailure`) para erros padronizados do `TEC.Core` (`Error.Validation`), com suporte nativo a formatação camelCase dos nomes de campos (`CamelCaseValidationFields`).
   - Mapeia a exceção `FluentValidation.ValidationException` (ex.: lançada por `ValidateAndThrow`) via `IExceptionErrorMapper`, assegurando que mesmo chamadas imperativas sejam convertidas em `Result` com HTTP 400 sem vazar stack trace.

## Consequências

### Positivas
- **Zero Comandos Sem Validação em Produção:** Com `.AddAspNetCore()`, a aplicação não sobe com comandos esquecidos sem validação.
- **Formatação Homogênea de Resposta:** Campos inválidos saem no padrão camelCase correspondente ao JSON da API (ex.: `itens[0].quantidade`), prontos para consumo por front-ends modernos.
- **Desacoplamento Tecnológico:** O núcleo `TEC.Cqrs` permanece livre de dependências de terceiros.

### Negativas / Limitações
- **Projetos Console/Workers Puros:** Fora do ASP.NET Core (`.AddAspNetCore()`), a verificação de ausência de validador ocorre na primeira execução do comando pelo `ValidationBehavior`.

## Referências
- Livro *CQRS na Prática*, Capítulo 09: *Validação*.
- Código: `TEC.Cqrs.Validation.*`, `TEC.Cqrs.Behaviors.ValidationBehavior`, `TEC.Cqrs.FluentValidation.*`.

---
⬅️ [ADR-0004](0004-autorizacao-em-tres-camadas-e-controle-granular-de-permissoes.md) · [🏛️ Índice de ADRs](README.md) · [ADR-0006](0006-gestao-transacional-com-comandos-aninhados-e-rollback-only.md) ➡️
