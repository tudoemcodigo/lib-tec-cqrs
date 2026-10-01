using Microsoft.Extensions.Logging;

namespace TEC.Cqrs.AspNetCore.Internal;

/// <summary>Mensagens de log da integração HTTP (continuação da numeração do <c>TEC.Cqrs</c>).</summary>
internal static partial class AspNetCoreLog
{
    /// <summary>Categoria dos logs (a mesma das versões anteriores).</summary>
    public const string Category = "TEC.Cqrs.AspNetCore.ResultHttpExtensions";

    [LoggerMessage(1011, LogLevel.Warning, "Cabeçalho Location descartado: a URL gerada não é um caminho relativo válido. A resposta segue sem Location.")]
    public static partial void InvalidLocation(ILogger logger);
}
