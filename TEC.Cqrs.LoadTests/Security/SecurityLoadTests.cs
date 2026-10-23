using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TEC.Cqrs.LoadGenerator;
using TEC.Cqrs.LoadTests.Infrastructure;
using TEC.Cqrs.SampleApi;

namespace TEC.Cqrs.LoadTests.Security;

/// <summary>
/// Segurança sob carga, de ponta a ponta pela API de exemplo (Kestrel real): sob concorrência, a autorização do pipeline
/// nunca mistura usuários nem deixa handler protegido executar sem permissão, e as respostas de erro nunca expõem
/// detalhes internos.
/// </summary>
[Category(TestCategories.LoadCi)]
[NotInParallel(nameof(SecurityLoadTests))]
public class SecurityLoadTests
{
    private static readonly ReportFile Reports = new("security", "Segurança sob carga");

    private const int Workers = 32;

    // Registra a primeira violação (para a mensagem) e conta todas
    private sealed class Violations
    {
        private readonly ConcurrentQueue<string> _samples = new();
        private int _count;

        public int Count => _count;

        public void Add(string description)
        {
            if (Interlocked.Increment(ref _count) <= 5)
                _samples.Enqueue(description);
        }

        public override string ToString() => string.Join(Environment.NewLine, _samples);
    }

    [Test]
    public async Task ConcurrentUsers_CrossUserAccess_NeverLeaksData()
    {
        // Risco: estado compartilhado entre requisições (usuário, escopo, cache) devolver o pedido de outro cliente
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(Workers);
        var violations = new Violations();

        await Parallel.ForAsync(0, 6_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            int customer = i % SampleData.DefaultCustomers;
            int owner = i % 2 == 0 ? customer : (customer + 1 + i % (SampleData.DefaultCustomers - 1)) % SampleData.DefaultCustomers;
            var orderId = SampleData.OrderId(owner, i % SampleData.DefaultOrdersPerCustomer);

            using var request = SampleScenarios.As(new HttpRequestMessage(HttpMethod.Get, $"/pedidos/{orderId}"), SampleData.CustomerId(customer));
            using var response = await client.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (owner == customer && (response.StatusCode != HttpStatusCode.OK || !body.Contains($"\"clienteId\":\"{SampleData.CustomerId(customer)}\"", StringComparison.Ordinal)))
                violations.Add($"{SampleData.CustomerId(customer)} não recebeu o próprio pedido: {(int)response.StatusCode} {body}");
            if (owner != customer && (response.StatusCode != HttpStatusCode.NotFound || body.Contains(SampleData.CustomerId(owner) + "\"", StringComparison.Ordinal)))
                violations.Add($"{SampleData.CustomerId(customer)} obteve dados do pedido de {SampleData.CustomerId(owner)}: {(int)response.StatusCode} {body}");
        });

        await Assert.That(violations.Count).IsEqualTo(0).Because(violations.ToString());
    }

    [Test]
    public async Task UnauthenticatedAndUnauthorizedFlood_ProtectedHandlersNeverRun()
    {
        // Rajada de requisições sem usuário, sem papel, com papel errado e com usuário inválido: o pipeline nega antes
        // da validação e o handler nunca executa (nada é gravado)
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(Workers);
        var violations = new Violations();

        await Parallel.ForAsync(0, 4_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            var order = new CreateOrderCommand($"Pedido {i}", 10m);
            (HttpRequestMessage Request, HttpStatusCode Expected) attempt = (i % 5) switch
            {
                0 => (Post("/pedidos", order), HttpStatusCode.Unauthorized),
                1 => (SampleScenarios.As(Post("/pedidos", order), SampleData.CustomerId(i % 100), roles: null), HttpStatusCode.Forbidden),
                2 => (SampleScenarios.As(Post($"/pedidos/{SampleData.OrderId(0, 0)}/estorno", null), SampleData.CustomerId(i % 100)), HttpStatusCode.Forbidden),
                3 => (SampleScenarios.As(Post("/pedidos/importacao", new ImportOrdersCommand([order])), "Usuario_Invalido!"), HttpStatusCode.Unauthorized),
                _ => (SampleScenarios.As(Post("/pedidos", order), SampleData.CustomerId(i % 100), roles: "SuperAdmin"), HttpStatusCode.Unauthorized),
            };

            using var request = attempt.Request;
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode != attempt.Expected)
                violations.Add($"tentativa {i % 5}: esperado {(int)attempt.Expected}, obtido {(int)response.StatusCode}");
        });

        await Assert.That(violations.Count).IsEqualTo(0).Because(violations.ToString());
        await Assert.That(host.Metrics.Executions("CreateOrderHandler")).IsEqualTo(0);
        await Assert.That(host.Metrics.Executions("ImportOrdersHandler")).IsEqualTo(0);
        await Assert.That(host.Metrics.Executions("RefundOrderHandler")).IsEqualTo(0);
        await Assert.That(host.Store.Created).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidInputWithoutPermission_DoesNotRevealValidationRules()
    {
        // Autorização antes da validação: quem não tem acesso recebe 401/403 sem os campos e as regras de validação
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(Workers);
        var violations = new Violations();
        var invalid = new CreateOrderCommand("", -1m);

        await Parallel.ForAsync(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            using var request = (i % 3) switch
            {
                0 => Post("/pedidos", invalid),
                1 => SampleScenarios.As(Post("/pedidos", invalid), SampleData.CustomerId(i % 100), roles: null),
                _ => SampleScenarios.As(Post("/pedidos", invalid), SampleData.CustomerId(i % 100)),
            };
            using var response = await client.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            bool authorized = i % 3 == 2;
            bool revealsRules = body.Contains("DESCRICAO_OBRIGATORIA", StringComparison.Ordinal) || body.Contains("VALOR_INVALIDO", StringComparison.Ordinal);
            if (authorized && (response.StatusCode != HttpStatusCode.BadRequest || !revealsRules))
                violations.Add($"usuário autorizado não recebeu os erros de validação: {(int)response.StatusCode} {body}");
            if (!authorized && (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) || revealsRules))
                violations.Add($"usuário sem permissão viu as regras de validação: {(int)response.StatusCode} {body}");
        });

        await Assert.That(violations.Count).IsEqualTo(0).Because(violations.ToString());
    }

    [Test]
    public async Task InternalErrorsUnderLoad_NeverExposeDetails()
    {
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(Workers);
        var violations = new Violations();

        await Parallel.ForAsync(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            using var request = SampleScenarios.As(Post("/diagnostico/falha", null), SampleData.CustomerId(i % 100));
            using var response = await client.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode != HttpStatusCode.InternalServerError)
                violations.Add($"esperado 500, obtido {(int)response.StatusCode}");
            if (body.Contains("segredo-interno", StringComparison.Ordinal) || body.Contains("InvalidOperationException", StringComparison.Ordinal)
                || body.Contains(" at ", StringComparison.Ordinal) || body.Contains(".cs:line", StringComparison.Ordinal))
                violations.Add($"resposta 500 expôs detalhes internos: {body}");
            if (!HasTraceId(body))
                violations.Add($"resposta 500 sem traceId para correlação: {body}");
        });

        await Assert.That(violations.Count).IsEqualTo(0).Because(violations.ToString());
        await Assert.That(host.Metrics.Executions("SimulateFailureHandler")).IsEqualTo(2_000);
    }

    [Test]
    public async Task MalformedOversizedAndHostileInput_Returns4xx_AndServerStaysHealthy()
    {
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(Workers);
        var violations = new Violations();
        string oversized = $"{{\"descricao\":\"{new string('x', SampleApiApp.MaxRequestBodyBytes + 1024)}\",\"valor\":1}}";
        int closedConnections = 0;

        await Parallel.ForAsync(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            using var request = (i % 6) switch
            {
                0 => Raw("/pedidos", "{\"descricao\": \"sem fim"),                                 // JSON malformado
                1 => Raw("/pedidos", oversized),                                                    // corpo acima do limite
                2 => Raw("/pedidos", "{\"descricao\":\"x\",\"valor\":\"não é número\"}"),          // tipo errado
                3 => Raw("/pedidos/importacao", $"{{\"itens\":[{string.Join(',', Enumerable.Repeat("{\"descricao\":\"x\",\"valor\":1}", 500))}]}}"), // itens demais
                4 => Raw("/pedidos", "{\"descricao\":\"x\",\"valor\":1e400}"),                       // número fora do intervalo
                _ => SampleScenarios.As(new HttpRequestMessage(HttpMethod.Get, $"/pedidos/{Guid.NewGuid()}"), new string('a', 10_000)), // usuário enorme
            };
            if (i % 6 is >= 0 and <= 4)
                SampleScenarios.As(request, SampleData.CustomerId(i % 100));

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (i % 6 == 1)
            {
                // Corpo acima do limite: o Kestrel responde 413 e pode fechar a conexão antes do fim do envio (recusado)
                Interlocked.Increment(ref closedConnections);
                return;
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (status is < 400 or >= 500)
                    violations.Add($"entrada hostil {i % 6}: esperado 4xx, obtido {status}");
            }
        });

        using var health = await client.GetAsync("/saude");
        LoadSettings.Report(Reports, "Entradas hostis", $"Conexões encerradas pelo servidor no corpo acima do limite: {closedConnections} de {2_000 / 6}{Environment.NewLine}");
        await Assert.That(violations.Count).IsEqualTo(0).Because(violations.ToString());
        await Assert.That(health.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.Store.Created).IsEqualTo(0);
    }

    private static bool HasTraceId(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("traceId", out var traceId) && traceId.GetString() is { Length: > 0 };
    }

    private static HttpRequestMessage Post(string path, object? body) =>
        new(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };

    private static HttpRequestMessage Raw(string path, string json) =>
        new(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
