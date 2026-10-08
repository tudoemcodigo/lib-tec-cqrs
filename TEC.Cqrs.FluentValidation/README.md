<div align="center">

<img src="https://raw.githubusercontent.com/tudoemcodigo/lib-tec-cqrs/main/Images/Logo.png" alt="TEC.Cqrs" width="100" />

# ✅ TEC.Cqrs.FluentValidation

**Executa os `AbstractValidator<T>` do FluentValidation no pipeline do TEC.Cqrs: a entrada é verificada antes da regra de negócio e cada campo inválido vira um erro claro (HTTP 400).**

[📚 Documentação do pacote](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/validacao.md) · [📚 TEC.Cqrs](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/README.md) · [🐙 Repositório](https://github.com/tudoemcodigo/lib-tec-cqrs)

</div>

## ✨ O que é

- **`.AddFluentValidation()`**: registra os validators dos assemblies informados ao `AddTecCqrs` e os liga ao behavior de
  validação (depois da autorização, antes do handler). Todos os validators da requisição rodam; havendo erro, o handler
  não é chamado.
- Um `Error.Validation` por falha, com o campo em camelCase (`endereco.cep`), igual ao JSON da API.
- Converte a `FluentValidation.ValidationException` lançada no handler (`ValidateAndThrow`) em falha de validação.
- Registro explícito (`AddValidator<TRequest, TValidator>()`) compatível com Native AOT.

## 🎯 Quando usar

Aplicações que validam a entrada com FluentValidation. Sem ele, use `IRequestValidator<T>` do próprio `TEC.Cqrs`.

## 📥 Instalação

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.Cqrs.FluentValidation --version 0.0.1
```

> [!IMPORTANT]
> **Use a mesma versão do `TEC.Cqrs` (e do `TEC.Cqrs.AspNetCore`, se houver).** Este pacote usa tipos internos do núcleo
> (`InternalsVisibleTo`) e é publicado junto com ele a cada release; versões diferentes podem falhar em execução
> (`MissingMethodException`, `TypeLoadException`). O pacote já traz o `TEC.Cqrs` na versão certa.

## 🚀 Início rápido

```csharp
using FluentValidation;
using TEC.Cqrs.DependencyInjection;

internal sealed class CreateCustomerValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithErrorCode("NOME_OBRIGATORIO").WithMessage("Nome é obrigatório.");
        RuleFor(c => c.Document).Length(11).WithMessage("Documento inválido."); // texto fixo: não ecoa o valor
    }
}

// Varredura (JIT)
builder.Services.AddTecCqrs(options => options.RegisterServicesFromAssemblyContaining<Program>())
    .AddFluentValidation();

// Ou registro explícito (Native AOT)
builder.Services.AddTecCqrs(options => options.AddCommandHandler<CreateCustomerCommand, Guid, CreateCustomerHandler>())
    .AddFluentValidation(fv => fv.AddValidator<CreateCustomerCommand, CreateCustomerValidator>());
```

> [!CAUTION]
> As mensagens vão para o cliente. Os placeholders `{PropertyValue}` e `{ComparisonValue}` ecoam o valor recebido: em
> campos sensíveis (senha, token, documento), use `WithMessage` com texto fixo.

## 📚 Documentação

Validação obrigatória, validators de tipo base/interface, `CamelCaseValidationFields` e erros:
[docs/validacao.md](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/docs/validacao.md).

`net8.0` e `net10.0` · Native AOT · [MIT](https://github.com/tudoemcodigo/lib-tec-cqrs/blob/main/LICENSE) · Roberto Oliveira, equipe Tudo em Código
