namespace TEC.Cqrs.LoadTests;

/// <summary>Categorias dos testes deste projeto, num único lugar (usadas nos filtros <c>[Category=...]</c> do CI).</summary>
public static class TestCategories
{
    /// <summary>Concorrência e fumaça de carga (segundos): só sob demanda (performance.yml, suíte rapida ou todas) ou localmente.</summary>
    public const string LoadCi = "Carga-CI";

    /// <summary>Soak, volume e carga sustentada (minutos): <c>[Explicit]</c>, só sob demanda (performance.yml) ou localmente com filtro.</summary>
    public const string LoadHeavy = "Carga-Pesada";
}
