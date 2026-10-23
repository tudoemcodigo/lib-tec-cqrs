namespace TEC.Cqrs.SampleApi;

/// <summary>
/// Dados sintéticos da API de exemplo: clientes <c>cliente-0</c> a <c>cliente-(N-1)</c>, cada um com pedidos de ids
/// determinísticos, para que o gerador de carga saiba quais pedidos pertencem a quem sem consultar a API.
/// </summary>
public static class SampleData
{
    /// <summary>Clientes cadastrados (padrão de <c>Exemplo:Clientes</c>).</summary>
    public const int DefaultCustomers = 100;

    /// <summary>Pedidos iniciais por cliente (padrão de <c>Exemplo:PedidosPorCliente</c>).</summary>
    public const int DefaultOrdersPerCustomer = 20;

    /// <summary>Pedidos criados mantidos por cliente (padrão de <c>Exemplo:MaxPedidosPorCliente</c>), além dos iniciais; os mais antigos saem primeiro.</summary>
    public const int DefaultMaxOrdersPerCustomer = 1_000;

    /// <summary>Usuário administrador usado nos cenários de estorno.</summary>
    public const string Admin = "admin-0";

    /// <summary>Id (e usuário) do cliente <paramref name="index"/>.</summary>
    public static string CustomerId(int index) => $"cliente-{index}";

    /// <summary>Id determinístico do pedido inicial <paramref name="order"/> do cliente <paramref name="customer"/>.</summary>
    public static Guid OrderId(int customer, int order) => new(customer, (short)order, 0x5EED, 0, 0, 0, 0, 0, 0, 0, 1);
}
