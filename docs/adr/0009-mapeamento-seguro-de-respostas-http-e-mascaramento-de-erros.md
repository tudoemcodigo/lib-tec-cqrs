[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0009

# ADR-0009: Mapeamento Seguro de Respostas HTTP, Sanitização de Erros 500/502 e Proteção de Cabeçalho Location

## Status
`Aceito`

## Contexto e Problema
A conversão de resultados de domínio e erros de aplicação para HTTP é uma fonte frequente de vulnerabilidades em APIs REST:
1. **Vazamento de Dados Internos (CWE-209):** Exceções não tratadas e erros 500 frequentemente expõem tabelas de banco, queries SQL, strings de conexão ou stack traces para o usuário final, facilitando ataques dirigidos.
2. **Redirecionamento Aberto e Injeção de Cabeçalhos no `Location` (CWE-113 / CWE-601):** Ao responder `201 Created`, construir o cabeçalho `Location` concatenando identificadores crus sem escape pode permitir injeção de quebras de linha (`\r\n`) ou redirecionamentos maliciosos para domínios externos (`//attacker.com`).
3. **Incompatibilidade com OpenAPI e Native AOT:** Respostas de API dinâmicas sem tipagem estrita geram esquemas Swagger/OpenAPI incompletos ou falham na serialização JSON quando o trimming de código do Native AOT remove metadados de reflexão.

O livro **CQRS na Prática** (*Capítulo 18: Integração com ASP.NET Core e Workers* e *Capítulo 22: Segurança em aplicações CQRS*) enfatiza a sanitização estrita de saídas e a conformidade do envelope de resposta.

## Decisão
Implementou-se a camada de transporte HTTP no pacote satélite `TEC.Cqrs.AspNetCore`, com os seguintes mecanismos de proteção:

1. **Envelope Único Padronizado (`ApiResponseHttpResult`):**
   - Utiliza as primitivas `ApiResponse`, `ApiResponse<T>` e `PagedResponse<T>` do `TEC.Core`.
   - Implementa simultaneamente `IResult` (para Minimal APIs do ASP.NET Core) e `IActionResult` (para Controllers tradicionais), permitindo interoperabilidade irrestrita.
   - Implementa `IEndpointMetadataProvider`: expõe automaticamente para ferramentas OpenAPI os códigos HTTP esperados (200/201 e 400, 401, 403, 404, 409, 422, 429, 500, 502) com os esquemas JSON rigorosos.
2. **Sanitização Ativa de Erros Internos:**
   - Erros do tipo `ErrorType.Failure` (500) e `ErrorType.ExternalService` (502) têm suas mensagens e detalhes **mascarados por padrão**. O cliente recebe uma mensagem genérica e segura. O log do pipeline registra o tipo e os códigos do erro (a mensagem do `Error` não é registrada) e, quando há exceção associada, a exceção completa; a `Activity` recebe apenas o tipo da exceção, salvo `RecordExceptionDetailsInTraces`.
   - O envelope de erro anexa automaticamente o identificador `traceId` (`Activity.Current.TraceId` ou `httpContext.TraceIdentifier`), permitindo suporte técnico e correlação instantânea em logs sem expor dados confidenciais.
3. **Higienização Estrita do Cabeçalho `Location` (`ToCreatedHttpResult`):**
   - Apenas URLs relativas iniciadas com barra única (`/`) são aceitas.
   - URLs absolutas (`http://...`), esquemas relativos a protocolo (`//host`), barras invertidas (`\`) ou caracteres de controle (`\r`, `\n`) são sumariamente bloqueados.
   - Os caracteres fora do permitido são escapados em UTF-8 (`%XX`) pelo próprio componente, preservando os delimitadores e as sequências `%XX` já escapadas. Se a validação falhar, a resposta 201 é emitida com segurança, o cabeçalho malicioso é suprimido e um aviso é gerado no log.
4. **Middleware de Exceção Global:**
   - Integra-se ao `SuppressDiagnosticsCallback` do `UseExceptionHandler` do ASP.NET Core para evitar logs duplicados de exceções já convertidas pelo pipeline.
   - Durante o desenvolvimento, o alvo .NET 8 usava um middleware próprio (`TecExceptionHandlerMiddleware`) com o mesmo comportamento; ele saiu antes da primeira publicação, junto com o alvo `net8.0` ([ADR-0011](0011-alvo-unico-net10-e-verificacao-native-aot-por-publicacao.md)).

## Consequências

### Positivas
- **Erros internos sem detalhes:** Erros `Failure` e `ExternalService` (e exceções não mapeadas) chegam ao cliente só com a mensagem genérica. Mensagens de erros de outros tipos (`Validation`, `NotFound`, `Conflict`...) são enviadas como a aplicação as escreveu.
- **`Location` restrito a caminho relativo:** URL absoluta, `//host`, barra invertida e caracteres de controle não geram o cabeçalho.
- **Correlação por `traceId`:** Toda resposta de falha leva o `traceId` da `Activity` atual (ou o `TraceIdentifier`), para localizar o registro nos logs e traces.

### Negativas / Limitações
- **Personalização de Detalhes 500:** Em ambiente de desenvolvimento local, desenvolvedores habituados a ver o stack trace na tela do browser devem consultar o console de log ou os traces do OpenTelemetry.

## Referências
- Livro *CQRS na Prática*, Capítulo 18: *Integração com ASP.NET Core e Workers*.
- Livro *CQRS na Prática*, Capítulo 22: *Segurança em aplicações CQRS*.
- ADR-0002 do `TEC.Core`: *Envelope Padronizado de Respostas de API e Mascaramento de Erros Internos*.
- Código: `TEC.Cqrs.AspNetCore.ResultHttpExtensions`, `TEC.Cqrs.AspNetCore.ApiResponseHttpResult`.

---
⬅️ [ADR-0008](0008-idempotencia-http-com-reserva-concorrente-e-hash-criptografico.md) · [🏛️ Índice de ADRs](README.md) · [ADR-0010](0010-observabilidade-nativa-com-opentelemetry-e-metricas-dimensionadas.md) ➡️
