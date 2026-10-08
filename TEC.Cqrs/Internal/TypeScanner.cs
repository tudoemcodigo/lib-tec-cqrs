using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TEC.Cqrs.Internal;

/// <summary>Varredura de assemblies em busca de requisições, handlers e authorizers.</summary>
/// <remarks>Baseada em reflexão: incompatível com trimming. O caminho compatível com Native AOT é o registro explícito.</remarks>
internal static class TypeScanner
{
    public const string ScanningMessage =
        "A varredura de assemblies usa reflexão e pode não encontrar tipos removidos pelo trimming. Em apps com trimming " +
        "ou Native AOT, registre os tipos explicitamente (AddCommandHandler, AddQueryHandler, AddNotificationHandler, " +
        "AddRequestAuthorizer, AddRequestValidator).";

    public const string DynamicCodeMessage =
        "O registro por varredura cria tipos genéricos em tempo de execução (MakeGenericType). Em Native AOT, use o " +
        "registro explícito.";

    /// <summary>
    /// Tipos concretos (não abstratos e não genéricos abertos) do assembly, ordenados pelo nome completo. A ordem
    /// determinística define a ordem de registro (e, portanto, de execução dos notification handlers).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Algum tipo do assembly não pôde ser carregado (ex.: dependência ausente). Ignorá-lo poderia deixar handlers,
    /// validators ou authorizers sem registro, sem nenhum aviso; a mensagem lista os erros de carregamento.
    /// </exception>
    [RequiresUnreferencedCode(ScanningMessage)]
    public static IEnumerable<Type> GetConcreteTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            const int maxListed = 10;
            var errors = ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message).Distinct().ToArray();
            string list = string.Join(Environment.NewLine, errors.Take(maxListed).Select(m => $"  - {m}"));
            if (errors.Length > maxListed)
                list += $"{Environment.NewLine}  ... e mais {errors.Length - maxListed}.";

            throw new InvalidOperationException(
                $"Não foi possível carregar todos os tipos do assembly '{assembly.FullName}' para registrar handlers, " +
                "validators e authorizers. Verifique se as dependências do assembly estão disponíveis. Erros de carregamento:" +
                Environment.NewLine + list, ex);
        }

        return types
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    /// <summary>Interfaces genéricas fechadas de <paramref name="type"/> cuja definição é <paramref name="openGeneric"/>.</summary>
    [RequiresUnreferencedCode(ScanningMessage)]
    public static IEnumerable<Type> GetClosedInterfaces(Type type, Type openGeneric) =>
        type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric);
}
