using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using TEC.Core.Common.Results;

namespace TEC.Cqrs.Internal;

/// <summary>
/// Cria falhas de <typeparamref name="TResponse"/> (<see cref="Result"/> ou <see cref="Result{T}"/>)
/// dentro de behaviors genéricos, que não conhecem o tipo concreto da resposta.
/// </summary>
/// <remarks>
/// Sem reflexão: <see cref="Result"/> é conhecido; a fábrica de cada <see cref="Result{T}"/> é registrada junto com o
/// handler (<see cref="ResultFactory.Register{TValue}"/>, chamado pelo registro explícito e pela varredura). Uma resposta
/// não registrada (handler adicionado direto no container) usa reflexão quando há geração de código em tempo de execução
/// (JIT); em Native AOT, lança <see cref="InvalidOperationException"/> pedindo o registro pelo <c>AddTecCqrs</c>.
/// </remarks>
internal static class ResultFactory<TResponse>
    where TResponse : Result
{
    private static Func<Error[], TResponse>? _factory = typeof(TResponse) == typeof(Result)
        ? (Func<Error[], TResponse>)(object)new Func<Error[], Result>(Result.Failure)
        : null;

    public static TResponse Failure(IEnumerable<Error> errors) => (Volatile.Read(ref _factory) ?? CreateFactory())([.. errors]);

    public static void Register(Func<Error[], TResponse> factory) => Interlocked.CompareExchange(ref _factory, factory, null);

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Só usa reflexão quando RuntimeFeature.IsDynamicCodeSupported é true (verificado abaixo); o .NET 8 não reconhece essa verificação.")]
    private static Func<Error[], TResponse> CreateFactory()
    {
        bool isGenericResult = typeof(TResponse).IsGenericType && typeof(TResponse).GetGenericTypeDefinition() == typeof(Result<>);
        if (!isGenericResult)
        {
            throw new InvalidOperationException(
                $"O tipo de resposta '{typeof(TResponse)}' não é suportado. Use Result ou Result<T>.");
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new InvalidOperationException(
                $"A resposta '{typeof(TResponse)}' não foi registrada. Em Native AOT, registre o handler pelo AddTecCqrs " +
                "(AddCommandHandler/AddQueryHandler ou varredura do assembly), e não direto no container.");
        }

        ResultFactory.RegisterDynamic(typeof(TResponse).GetGenericArguments()[0]);
        return Volatile.Read(ref _factory)!;
    }
}

/// <summary>Registro das fábricas de falha de <see cref="Result{T}"/>.</summary>
internal static class ResultFactory
{
    public static void Register<TValue>() =>
        ResultFactory<Result<TValue>>.Register(static errors => Result<TValue>.Failure(errors));

    /// <summary>Registro a partir do tipo do valor (varredura de assemblies e fallback em JIT).</summary>
    [RequiresDynamicCode("Cria Result<T> para um tipo conhecido apenas em tempo de execução.")]
    public static void RegisterDynamic(Type valueType) =>
        typeof(ResultFactory)
            .GetMethod(nameof(Register), BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)!
            .MakeGenericMethod(valueType)
            .Invoke(null, null);
}
