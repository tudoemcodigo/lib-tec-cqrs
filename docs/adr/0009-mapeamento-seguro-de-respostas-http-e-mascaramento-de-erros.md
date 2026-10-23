[🏛️ TEC.Cqrs](../../README.md) ➔ [📖 Documentação](../README.md) ➔ [ADRs](README.md) ➔ ADR-0009

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
   - Erros do tipo `ErrorType.Failure` (500) e `ErrorType.ExternalService` (502) têm suas mensagens e detalhes **mascarados por padrão**. O cliente recebe uma mensagem genérica e segura, enquanto os detalhes reais e a exceção associada são despachados exclusivamente para o logger corporativo e para a `Activity` de telemetria.
   - O envelope de erro anexa automaticamente o identificador `traceId` (`Activity.Current.TraceId` ou `httpContext.TraceIdentifier`), permitindo suporte técnico e correlação instantânea em logs sem expor dados confidenciais.
3. **Higienização Estrita do Cabeçalho `Location` (`ToCreatedHttpResult`):**
   - Apenas URLs relativas iniciadas com barra única (`/`) são aceitas.
   - URLs absolutas (`http://...`), esquemas relativos a protocolo (`//host`), barras invertidas (`\`) ou caracteres de controle (`\r`, `\n`) são sumariamente bloqueados.
   - Os segmentos são escapados individualmente via `Uri.EscapeDataString` (preservando sequências válidas `%XX`). Se a validação falhar, a resposta 201 é emitida com segurança, o cabeçalho malicioso é suprimido e um aviso é gerado no log.
4. **Middleware de Exceção Global com Compatibilidade .NET 8 e .NET 10:**
   - Em .NET 10, integra-se ao `SuppressDiagnosticsCallback` nativo para evitar logs duplicados de exceções já convertidas pelo pipeline.
   - Em .NET 8, fornece o `TecExceptionHandlerMiddleware` que emula o mesmo comportamento sem poluição de logs.

## Consequências

### Positivas
- **Zero Vazamento de Infraestrutura:** Detalhes de falhas de banco ou serviços externos nunca chegam ao consumidor da API.
- **Prevenção Total de Open Redirect / CRLF Injection:** Cabeçalhos `Location` blindados por design.
- **Rastreabilidade Total:** Todo erro em produção possui `traceId` navegável no OpenTelemetry / Grafana / Seq.

### Negativas / Limitações
- **Personalização de Detalhes 500:** Em ambiente de desenvolvimento local, desenvolvedores habituados a ver o stack trace na tela do browser devem consultar o console de log ou os traces do OpenTelemetry.

## Referências
- Livro *CQRS na Prática*, Capítulo 18: *Integração com ASP.NET Core e Workers*.
- Livro *CQRS na Prática*, Capítulo 22: *Segurança em aplicações CQRS*.
- ADR-0002 do `TEC.Core`: *Envelope Padronizado de Respostas de API e Mascaramento de Erros Internos*.
- Código: `TEC.Cqrs.AspNetCore.ResultHttpExtensions`, `TEC.Cqrs.AspNetCore.ApiResponseHttpResult`.
