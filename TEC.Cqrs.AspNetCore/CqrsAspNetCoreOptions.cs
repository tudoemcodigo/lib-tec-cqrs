using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TEC.Cqrs.AspNetCore;

/// <summary>Configuração do <c>AddAspNetCore</c>.</summary>
public sealed class CqrsAspNetCoreOptions
{
    /// <summary>
    /// Metadados JSON (System.Text.Json gerado em tempo de compilação) das respostas <c>ApiResponse&lt;T&gt;</c> da
    /// aplicação, normalmente o <c>Default</c> de um <see cref="JsonSerializerContext"/>. Obrigatório em apps com trimming
    /// ou Native AOT; opcional nos demais.
    /// </summary>
    /// <remarks>
    /// <para>Sem resolvedor (padrão), as respostas usam o <c>JsonDefaults.Options</c> do TEC.Core (reflexão), enquanto a
    /// reflexão do System.Text.Json estiver habilitada (padrão em apps sem trimming).</para>
    /// <para>Com resolvedor, as respostas usam as mesmas convenções (camelCase, ignora nulos, acentos sem escape) com os
    /// metadados informados; o <c>ApiResponse</c> sem dados (falhas, exception handler) já vem incluído. Registre no contexto
    /// todos os envelopes retornados pelos endpoints (<c>ApiResponse&lt;ClienteDto&gt;</c>,
    /// <c>PagedResponse&lt;ClienteDto&gt;</c> etc.) e os enums com <c>JsonStringEnumConverter&lt;TEnum&gt;</c>.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [JsonSerializable(typeof(ApiResponse&lt;ClienteDto&gt;))]
    /// [JsonSerializable(typeof(PagedResponse&lt;ClienteDto&gt;))]
    /// internal sealed partial class AppJsonContext : JsonSerializerContext;
    ///
    /// .AddAspNetCore(http => http.JsonTypeInfoResolver = AppJsonContext.Default);
    /// </code>
    /// </example>
    public IJsonTypeInfoResolver? JsonTypeInfoResolver { get; set; }

    /// <summary>
    /// Substitui um <c>IPrincipalAccessor</c> registrado antes do <c>AddAspNetCore</c> pelo que lê o <c>HttpContext.User</c>.
    /// Padrão <c>false</c>: com um accessor registrado antes, o <c>AddAspNetCore</c> lança
    /// <see cref="InvalidOperationException"/>, para a autorização HTTP não usar outro usuário em silêncio.
    /// </summary>
    /// <remarks>
    /// Para manter o seu accessor também nas requisições HTTP (ex.: usuário de um token de serviço), não use esta opção:
    /// registre-o depois do <c>AddAspNetCore</c> com <c>services.Replace(ServiceDescriptor.Scoped&lt;IPrincipalAccessor, MeuAccessor&gt;())</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddScoped&lt;IPrincipalAccessor, UsuarioDoJobAccessor&gt;(); // registrado por um módulo compartilhado
    /// services.AddTecCqrs(...).AddAspNetCore(http => http.ReplaceExistingPrincipalAccessor = true);
    /// </code>
    /// </example>
    public bool ReplaceExistingPrincipalAccessor { get; set; }
}
