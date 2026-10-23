using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using TEC.Core.Common.Results;
using TEC.Core.Exceptions;
using TEC.Core.Responses.Pagination;
using TEC.Cqrs.AspNetCore;

namespace TEC.Cqrs.Tests;

public class HttpResultTests
{
    private static async Task<(HttpContext Context, JsonElement Body)> ExecuteAsync(IResult result)
    {
        // O TUnit executa cada teste dentro de uma Activity; sem limpar, o traceId viria dela e não do TraceIdentifier
        Activity.Current = null;
        var context = new DefaultHttpContext { TraceIdentifier = "trace-123" };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context, document.RootElement.Clone());
    }

    [Test]
    public async Task Success_with_value_returns_200_and_data()
    {
        var (context, body) = await ExecuteAsync(Result.Success("Maria").ToHttpResult());

        await Assert.That(context.Response.StatusCode).IsEqualTo(200);
        await Assert.That(body.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("data").GetString()).IsEqualTo("Maria");
        await Assert.That(body.TryGetProperty("traceId", out _)).IsFalse();
    }

    [Test]
    public async Task Created_returns_201_and_location()
    {
        var id = Guid.NewGuid();

        var (context, body) = await ExecuteAsync(await Task.FromResult(Result.Success(id)).ToCreatedHttpResult(v => $"/clientes/{v}"));

        await Assert.That(context.Response.StatusCode).IsEqualTo(201);
        await Assert.That(context.Response.Headers.Location.ToString()).IsEqualTo($"/clientes/{id}");
        await Assert.That(body.GetProperty("data").GetGuid()).IsEqualTo(id);
    }

    [Test]
    public async Task Failed_Created_does_not_send_location()
    {
        var (context, _) = await ExecuteAsync(
            Result.Failure<Guid>(Error.Conflict("CLIENTE_DUPLICADO", "Cliente já existe.")).ToCreatedHttpResult(v => $"/clientes/{v}"));

        await Assert.That(context.Response.StatusCode).IsEqualTo(409);
        await Assert.That(context.Response.Headers.ContainsKey("Location")).IsFalse();
    }

    [Test]
    public async Task Validation_error_returns_400_with_fields_and_traceId()
    {
        var result = Result.Failure(
            Error.Validation("NOME_OBRIGATORIO", "Nome é obrigatório.", "nome"),
            Error.Validation("CPF_INVALIDO", "CPF inválido.", "cpf"));

        var (context, body) = await ExecuteAsync(result.ToHttpResult());

        await Assert.That(context.Response.StatusCode).IsEqualTo(400);
        await Assert.That(body.GetProperty("errors").GetArrayLength()).IsEqualTo(2);
        await Assert.That(body.GetProperty("errors")[1].GetProperty("field").GetString()).IsEqualTo("cpf");
        await Assert.That(body.GetProperty("traceId").GetString()).IsEqualTo("trace-123");
    }

    [Test]
    public async Task Internal_failure_returns_500_without_exposing_details()
    {
        var result = Result.Failure(Error.Failure("DB_ERRO", "Timeout em sql-prod-01"));

        var (context, body) = await ExecuteAsync(result.ToHttpResult());

        await Assert.That(context.Response.StatusCode).IsEqualTo(500);
        await Assert.That(body.GetProperty("errors").GetArrayLength()).IsEqualTo(0);
        await Assert.That(body.GetRawText()).DoesNotContain("sql-prod-01");
    }

    [Test]
    public async Task Paged_result_returns_pagination_block()
    {
        Result<PagedResult<string>> result = new PagedResult<string>(["Ana", "Bia"], 1, 2, 5);

        var (_, body) = await ExecuteAsync(result.ToHttpResult());

        await Assert.That(body.GetProperty("data").GetArrayLength()).IsEqualTo(2);
        await Assert.That(body.GetProperty("pagination").GetProperty("totalPages").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Exception_handler_converts_AppException_to_correct_status()
    {
        var context = CreateContextWithException(new NotFoundException("CLIENTE_NAO_ENCONTRADO", "Cliente não encontrado."));

        await ApplicationBuilderExtensions.HandleExceptionAsync(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task Exception_handler_does_not_expose_unknown_exception_details()
    {
        var context = CreateContextWithException(new InvalidOperationException("connection string secreta"));

        await ApplicationBuilderExtensions.HandleExceptionAsync(context);

        context.Response.Body.Position = 0;
        string json = await new StreamReader(context.Response.Body).ReadToEndAsync();
        await Assert.That(context.Response.StatusCode).IsEqualTo(500);
        await Assert.That(json).DoesNotContain("secreta");
    }

    private static DefaultHttpContext CreateContextWithException(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Features.Set<IExceptionHandlerFeature>(new ExceptionHandlerFeature { Error = exception, Path = "/" });
        return context;
    }
}
