using System.Globalization;

namespace TEC.Cqrs.LoadTests.Infrastructure;

/// <summary>
/// Parâmetros e relatórios dos testes de carga. Os volumes e durações dos testes pesados podem ser ajustados por
/// variáveis de ambiente, sem recompilar: cada variável específica (ex.: TEC_CARGA_SOAK_SEGUNDOS=600) define a base, e
/// TEC_CARGA_FATOR multiplica todas as bases de uma vez (ex.: 0.1 para uma rodada rápida, 5 para uma longa).
/// </summary>
public static class LoadSettings
{
    /// <summary>
    /// Chave de <c>[NotInParallel]</c> dos testes que medem tempo, memória, handles ou vazão: rodam um de cada vez, para
    /// que a carga de um não distorça a medição do outro. Não é uma categoria.
    /// </summary>
    public const string MeasurementKey = "load-measurement";

    private static readonly object ReportLock = new();
    private static readonly HashSet<string> StartedReports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fator único de duração e volume dos testes pesados (TEC_CARGA_FATOR, número invariante maior que zero; ausente ou
    /// inválido = 1).
    /// </summary>
    public static double Factor =>
        double.TryParse(Environment.GetEnvironmentVariable("TEC_CARGA_FATOR"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && double.IsFinite(value) && value > 0
            ? value
            : 1;

    /// <summary>Requisições enviadas no teste de volume (base TEC_CARGA_ENVIOS, padrão 2 milhões, × fator).</summary>
    public static int Sends => Scale(GetInt("TEC_CARGA_ENVIOS", 2_000_000), minimum: 1_000);

    /// <summary>Commands internos em uma única transação no teste de volume (base TEC_CARGA_COMMANDS_INTERNOS, padrão 50 mil, × fator).</summary>
    public static int NestedCommands => Scale(GetInt("TEC_CARGA_COMMANDS_INTERNOS", 50_000), minimum: 1_000);

    /// <summary>Notificações pós-commit de um único command no teste de volume (base TEC_CARGA_NOTIFICACOES, padrão 100 mil, × fator).</summary>
    public static int Notifications => Scale(GetInt("TEC_CARGA_NOTIFICACOES", 100_000), minimum: 1_000);

    /// <summary>Duração do soak em processo (base TEC_CARGA_SOAK_SEGUNDOS, padrão 120 s, × fator).</summary>
    public static TimeSpan SoakDuration => Seconds(GetInt("TEC_CARGA_SOAK_SEGUNDOS", 120));

    /// <summary>Duração da carga sustentada na API de exemplo (base TEC_CARGA_API_SEGUNDOS, padrão 60 s, × fator).</summary>
    public static TimeSpan ApiDuration => Seconds(GetInt("TEC_CARGA_API_SEGUNDOS", 60));

    /// <summary>Duração da carga só de escrita na API (base: a da API limitada a 30 s, × fator).</summary>
    public static TimeSpan WriteDuration => Seconds(Math.Min(GetInt("TEC_CARGA_API_SEGUNDOS", 60), 30));

    /// <summary>Duração de cada medição de vazão do pipeline (base 10 s, × fator).</summary>
    public static TimeSpan ThroughputDuration => Seconds(10);

    /// <summary>Requisições simultâneas na carga pesada da API (padrão 64; não é multiplicado pelo fator).</summary>
    public static int ApiConcurrency => GetInt("TEC_CARGA_API_CONCORRENCIA", 64);

    /// <summary>
    /// Pasta onde cada suíte grava o seu relatório em Markdown (o CI publica no resumo da execução).
    /// Sem a variável, os relatórios vão só para a saída do teste.
    /// </summary>
    public static string? ReportDirectory => Environment.GetEnvironmentVariable("TEC_CARGA_RELATORIOS");

    /// <summary>
    /// Escreve uma seção do relatório na saída do teste e, se TEC_CARGA_RELATORIOS estiver definida, no arquivo
    /// <c>&lt;suíte&gt;.md</c> da pasta (recriado na primeira seção de cada execução, com o título da suíte).
    /// </summary>
    /// <param name="file">Arquivo da suíte.</param>
    /// <param name="title">Título da seção.</param>
    /// <param name="body">Conteúdo em Markdown (linhas e tabelas).</param>
    public static void Report(ReportFile file, string title, string body)
    {
        ArgumentNullException.ThrowIfNull(file);
        Console.WriteLine($"## {title}{Environment.NewLine}{body}");
        if (ReportDirectory is not { Length: > 0 } directory)
            return;

        string path = Path.Combine(directory, file.Name + ".md");
        string section = $"## {title}{Environment.NewLine}{Environment.NewLine}{body.TrimEnd()}{Environment.NewLine}{Environment.NewLine}";

        // Testes de suítes diferentes podem terminar ao mesmo tempo: um lock para todos os arquivos basta (escrita rara)
        lock (ReportLock)
        {
            Directory.CreateDirectory(directory);
            if (StartedReports.Add(path))
                File.WriteAllText(path, $"# {file.Title}{Environment.NewLine}{Environment.NewLine}{section}");
            else
                File.AppendAllText(path, section);
        }
    }

    private static int Scale(int baseValue, int minimum) => (int)Math.Clamp(Math.Round(baseValue * Factor), minimum, int.MaxValue);

    private static TimeSpan Seconds(int baseSeconds) => TimeSpan.FromSeconds(Math.Max(baseSeconds * Factor, 1));

    private static int GetInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : defaultValue;
}

/// <summary>Arquivo de relatório de uma suíte de carga: <c>&lt;Name&gt;.md</c> em TEC_CARGA_RELATORIOS, com o título informado.</summary>
/// <param name="Name">Nome do arquivo, sem extensão (ex.: <c>soak</c>).</param>
/// <param name="Title">Título do relatório.</param>
public sealed record ReportFile(string Name, string Title);
