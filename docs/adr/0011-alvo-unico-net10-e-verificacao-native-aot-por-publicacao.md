[🏠 TEC.Cqrs](../../README.md) › [📚 Documentação](../README.md) › [🏛️ ADRs](README.md) › ADR-0011

# ADR-0011: Alvo Único .NET 10 e Verificação de Native AOT por Publicação Real

## Status
`Aceito` (0.0.1)

## Contexto e Problema
Durante o desenvolvimento da 0.0.1, os três pacotes tinham dois alvos, `net8.0` e `net10.0`. Manter o .NET 8 custava:

1. **Código duplicado por alvo:** um polyfill de `System.Threading.Lock` compilado só no núcleo (com `Compile Remove` nos
   satélites para evitar `CS0436`), equivalentes internos de `Activity.AddException` e `InstrumentAdvice`, e um middleware
   de exceções próprio (`TecExceptionHandlerMiddleware`, eventos 1012 a 1015) porque o `SuppressDiagnosticsCallback` só
   existe no .NET 10.
2. **Supressões de AOT:** o analisador do .NET 8 não reconhecia algumas guardas de `RuntimeFeature.IsDynamicCodeSupported`,
   o que levou a supressões de IL3050 no código.
3. **CI mais caro:** uma execução a mais dos testes por PR e por publicação, com o runtime 8 instalado no agente.

Além disso, a compatibilidade com Native AOT era afirmada só pelos analisadores de trimming/AOT. O núcleo ainda montava,
na primeira requisição de cada tipo, os alvos de authorizers de classes base e interfaces com `Type.GetInterfaces`,
`MakeGenericType` e `Activator`, com IL2090/IL3050 suprimidos, e nenhuma publicação real com `PublishAot` existia.

## Decisão
1. **Alvo único `net10.0`** (LTS), vindo do `build/Tec.Build.props` canônico do tec-workflows. Sem `#if NET*`, sem
   polyfills e sem condições por TFM no `Directory.Packages.props`. Aplicações em .NET 8 não são suportadas.
2. **Authorizers pré-calculados no registro:** o `CqrsRegistry` calcula, no `AddTecCqrs`, os authorizers que valem para
   cada requisição conhecida. Um authorizer de tipo base ou interface registrado direto no container (fora do
   `AddTecCqrs`) é recusado: antes da chamada, por ela; depois, na subida do host (`.AddAspNetCore()`) ou no primeiro
   `Send`. O suporte a esse registro tardio, que dependia de reflexão em execução, saiu.
3. **Publicação real no CI:** o job `Native AOT` do `ci.yml` publica o `samples/TEC.Cqrs.AotSample` com `PublishAot`,
   `-warnaserror` e `TreatWarningsAsErrors`, e executa o binário nativo, que confere o pipeline e sai com código 1 se algo
   falhar.

## Consequências

### Positivas
- Um único caminho de código por comportamento: o `UseTecExceptionHandler` é o do ASP.NET Core, sem emulação.
- O núcleo não tem mais supressões de IL2090/IL3050 para authorizers nem para o executor de handlers registrados direto no
  container; as guardas de `IsDynamicCodeSupported` são reconhecidas pelo analisador.
- Um aviso de trimming/AOT novo em qualquer código alcançável pelo sample quebra o CI, e o comportamento é conferido no
  binário nativo, não só deduzido da análise.

### Negativas / Limitações
- **Sem .NET 8:** aplicações em .NET 8 precisam migrar para o .NET 10 para usar os pacotes. Authorizers de interface
  registrados direto no container depois do `AddTecCqrs` são recusados: use `AddRequestAuthorizer` ou a varredura.
- O sample não usa o FluentValidation (biblioteca de terceiros sem anotações de trimming): o `TEC.Cqrs.FluentValidation`
  continua coberto só pelos analisadores do próprio pacote.
- O job `Native AOT` roda fora do `ci-ok` do workflow central; para bloquear o merge, ele precisa entrar na proteção da
  `main`.

## Referências
- Código: `TEC.Cqrs.Internal.CqrsRegistry`, `TEC.Cqrs.AspNetCore.ApplicationBuilderExtensions`,
  `samples/TEC.Cqrs.AotSample`, `.github/workflows/ci.yml` (job `aot`).
- [⚡ Native AOT e trimming](../aot.md).

---
⬅️ [ADR-0010](0010-observabilidade-nativa-com-opentelemetry-e-metricas-dimensionadas.md) · [🏛️ Índice de ADRs](README.md)
