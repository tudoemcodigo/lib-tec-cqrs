using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace TEC.Cqrs.LoadGenerator;

/// <summary>Cenários da API de exemplo (TEC.Cqrs.SampleApi), com os pesos da mistura padrão.</summary>
/// <remarks>
/// Os cenários de falha (validação, acesso a pedido de outro cliente, sem autenticação, sem papel) esperam o status de
/// erro correspondente: uma resposta 200 neles conta como erro, porque significaria uma falha de segurança.
/// </remarks>
public static class SampleScenarios
{
    // Mesmos dados sintéticos da API de exemplo (SampleData): clientes cliente-0..99 com 20 pedidos de ids determinísticos
    private const int Customers = 100;
    private const int OrdersPerCustomer = 20;
    private const string Admin = "admin-0";

    /// <summary>Todos os cenários. O de erro interno (500 com log de erro) tem peso 0: ligue-o explicitamente.</summary>
    public static IReadOnlyList<LoadScenario> All { get; } =
    [
        new("saude", 5, _ => Get("/saude")),
        new("obter", 30, r =>
        {
            int customer = r.Next(Customers);
            return As(Get($"/pedidos/{OrderId(customer, r.Next(OrdersPerCustomer))}"), Customer(customer));
        }),
        new("listar", 15, r => As(Get($"/pedidos?pagina={r.Next(1, 3)}&tamanho=10"), Customer(r.Next(Customers)))),
        new("criar", 15, r => As(Post("/pedidos", NewOrder(r)), Customer(r.Next(Customers))), ExpectedStatus: 201),
        new("importar", 5, r => As(Post("/pedidos/importacao", new ImportPayload([.. Enumerable.Range(0, 10).Select(_ => NewOrder(r))])), Customer(r.Next(Customers)))),
        // Último item inválido: os 9 anteriores são desfeitos com a transação (nada gravado, nenhum evento publicado)
        new("importar-invalido", 3, r => As(Post("/pedidos/importacao", new ImportPayload([.. Enumerable.Range(0, 9).Select(_ => NewOrder(r)), new OrderPayload("Inválido", 0m)])), Customer(r.Next(Customers))), ExpectedStatus: 400),
        new("erro-validacao", 5, r => As(Post("/pedidos", new OrderPayload("", -1m)), Customer(r.Next(Customers))), ExpectedStatus: 400),
        new("acesso-alheio", 10, r =>
        {
            int customer = r.Next(Customers);
            int other = (customer + 1 + r.Next(Customers - 1)) % Customers;
            return As(Get($"/pedidos/{OrderId(other, r.Next(OrdersPerCustomer))}"), Customer(customer));
        }, ExpectedStatus: 404),
        new("nao-autenticado", 5, r => Post("/pedidos", NewOrder(r)), ExpectedStatus: 401),
        new("sem-papel", 3, r => As(Post("/pedidos", NewOrder(r)), Customer(r.Next(Customers)), roles: null), ExpectedStatus: 403),
        new("estorno-negado", 2, r => As(Post($"/pedidos/{OrderId(r.Next(Customers), 0)}/estorno", null), Customer(r.Next(Customers))), ExpectedStatus: 403),
        new("estorno", 2, r => As(Post($"/pedidos/{OrderId(r.Next(Customers), 0)}/estorno", null), Admin, roles: "Admin")),
        new("erro-interno", 0, r => As(Post("/diagnostico/falha", null), Customer(r.Next(Customers))), ExpectedStatus: 500),
    ];

    /// <summary>
    /// Seleciona cenários por nome, com peso opcional: <c>"obter,criar:50,erro-interno:1"</c>. Vazio: mistura padrão.
    /// </summary>
    public static IReadOnlyList<LoadScenario> Parse(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return All;

        var selected = new List<LoadScenario>();
        foreach (var item in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', 2);
            var scenario = All.FirstOrDefault(s => s.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Cenário desconhecido: {parts[0]}. Disponíveis: {string.Join(", ", All.Select(s => s.Name))}.");

            int weight = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : Math.Max(scenario.Weight, 1);
            selected.Add(scenario with { Weight = weight });
        }

        return selected;
    }

    /// <summary>Id do pedido inicial <paramref name="order"/> do cliente <paramref name="customer"/> (igual ao SampleData da API).</summary>
    public static Guid OrderId(int customer, int order) => new(customer, (short)order, 0x5EED, 0, 0, 0, 0, 0, 0, 0, 1);

    /// <summary>Usuário do cliente <paramref name="index"/>.</summary>
    public static string Customer(int index) => $"cliente-{index}";

    /// <summary>Identifica a requisição como <paramref name="user"/> (autenticação de exemplo por cabeçalho).</summary>
    public static HttpRequestMessage As(HttpRequestMessage request, string user, string? roles = "Cliente")
    {
        request.Headers.Add("X-Usuario", user);
        if (roles is not null)
            request.Headers.Add("X-Papeis", roles);
        return request;
    }

    private static OrderPayload NewOrder(Random random) => new($"Pedido {random.Next()}", random.Next(1, 100_000) / 100m);

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, path);

    private static HttpRequestMessage Post(string path, object? body) =>
        new(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };

    // Corpo JSON de um pedido: os nomes no fio são os da API (descricao, valor)
    private sealed record OrderPayload(
        [property: JsonPropertyName("descricao")] string Description,
        [property: JsonPropertyName("valor")] decimal Amount);

    // Corpo JSON da importação (itens)
    private sealed record ImportPayload([property: JsonPropertyName("itens")] IReadOnlyList<OrderPayload> Items);
}
