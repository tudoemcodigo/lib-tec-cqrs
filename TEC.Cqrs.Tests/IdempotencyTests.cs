using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TEC.Core.Common.Results;
using TEC.Cqrs.Abstractions;
using TEC.Cqrs.AspNetCore;
using TEC.Cqrs.AspNetCore.Idempotency;
using TEC.Cqrs.DependencyInjection;
using TEC.Cqrs.Idempotency;
using TEC.Cqrs.Tests.Fakes;

namespace TEC.Cqrs.Tests;

/// <summary>Relógio manual para os testes de expiração.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

public class InMemoryIdempotencyStoreTests
{
    private static readonly IdempotencyKey Key = new("usuario-1", "chave-1");

    private static IdempotencyRequest Request(string hash = "H1", int lockSeconds = 60) =>
        new(Key, hash, TimeSpan.FromHours(1), TimeSpan.FromSeconds(lockSeconds));

    private static IdempotentResponse Response(string body = "ok") =>
        new(201, "application/json", new Dictionary<string, string> { ["Location"] = "/x/1" }, Encoding.UTF8.GetBytes(body));

    [Test]
    public async Task First_request_starts_and_concurrent_one_sees_in_progress()
    {
        var store = new InMemoryIdempotencyStore();

        var first = await store.TryBeginAsync(Request(), CancellationToken.None);
        var second = await store.TryBeginAsync(Request(), CancellationToken.None);

        await Assert.That(first.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(second.Status).IsEqualTo(IdempotencyBeginStatus.InProgress);
    }

    [Test]
    public async Task Completed_response_is_replayed_and_different_hash_is_mismatch()
    {
        var store = new InMemoryIdempotencyStore();
        var begin = await store.TryBeginAsync(Request(), CancellationToken.None);
        await store.CompleteAsync(Key, begin.LockId, Response(), CancellationToken.None);

        var replay = await store.TryBeginAsync(Request(), CancellationToken.None);
        var other = await store.TryBeginAsync(Request("H2"), CancellationToken.None);

        await Assert.That(replay.Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That(Encoding.UTF8.GetString(replay.Response!.Body.Span)).IsEqualTo("ok");
        await Assert.That(replay.Response.Headers["location"]).IsEqualTo("/x/1");
        await Assert.That(other.Status).IsEqualTo(IdempotencyBeginStatus.Mismatch);
    }

    [Test]
    public async Task Abandon_releases_the_key()
    {
        var store = new InMemoryIdempotencyStore();
        var begin = await store.TryBeginAsync(Request(), CancellationToken.None);

        await Assert.That(await store.AbandonAsync(Key, begin.LockId, CancellationToken.None)).IsTrue();
        await Assert.That((await store.TryBeginAsync(Request(), CancellationToken.None)).Status).IsEqualTo(IdempotencyBeginStatus.Started);
    }

    [Test]
    public async Task Expired_lock_is_taken_over_and_old_owner_cannot_complete()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdempotencyStore(timeProvider: clock);
        var first = await store.TryBeginAsync(Request(lockSeconds: 10), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(11));
        var second = await store.TryBeginAsync(Request(lockSeconds: 10), CancellationToken.None);

        await Assert.That(second.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(await store.CompleteAsync(Key, first.LockId, Response(), CancellationToken.None)).IsFalse();
        await Assert.That(await store.AbandonAsync(Key, first.LockId, CancellationToken.None)).IsFalse();
        await Assert.That(await store.CompleteAsync(Key, second.LockId, Response(), CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Completed_response_expires_after_retention()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdempotencyStore(timeProvider: clock);
        var begin = await store.TryBeginAsync(Request(), CancellationToken.None);
        await store.CompleteAsync(Key, begin.LockId, Response(), CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

        await Assert.That((await store.TryBeginAsync(Request("H2"), CancellationToken.None)).Status).IsEqualTo(IdempotencyBeginStatus.Started);
    }

    [Test]
    public async Task Stored_response_is_a_defensive_copy()
    {
        var store = new InMemoryIdempotencyStore();
        var body = Encoding.UTF8.GetBytes("abc");
        var begin = await store.TryBeginAsync(Request(), CancellationToken.None);
        await store.CompleteAsync(Key, begin.LockId, new IdempotentResponse(200, null, new Dictionary<string, string>(), body), CancellationToken.None);

        body[0] = (byte)'X';
        var replay = await store.TryBeginAsync(Request(), CancellationToken.None);

        await Assert.That(Encoding.UTF8.GetString(replay.Response!.Body.Span)).IsEqualTo("abc");
    }

    [Test]
    public async Task Full_store_evicts_completed_entries_and_refuses_when_all_in_progress()
    {
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxEntries = 2 });
        var a = new IdempotencyKey("u", "a");
        var begin = await store.TryBeginAsync(new IdempotencyRequest(a, "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);
        await store.CompleteAsync(a, begin.LockId, Response(), CancellationToken.None);
        await store.TryBeginAsync(new IdempotencyRequest(new("u", "b"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);

        // "a" (concluída) sai para dar lugar a "c"
        await store.TryBeginAsync(new IdempotencyRequest(new("u", "c"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);
        await Assert.That(store.Count).IsEqualTo(2);

        await Assert.That(async () => await store.TryBeginAsync(
                new IdempotencyRequest(new("u", "d"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None))
            .ThrowsExactly<InvalidOperationException>();
    }

    private static async Task CompleteAsync(InMemoryIdempotencyStore store, string scope, string key)
    {
        var k = new IdempotencyKey(scope, key);
        var begin = await store.TryBeginAsync(new IdempotencyRequest(k, "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);
        await store.CompleteAsync(k, begin.LockId, Response(), CancellationToken.None);
    }

    private static ValueTask<IdempotencyBeginResult> BeginAsync(InMemoryIdempotencyStore store, string scope, string key) =>
        store.TryBeginAsync(new IdempotencyRequest(new(scope, key), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);

    [Test]
    public async Task Scope_quota_evicts_only_the_entries_of_the_same_scope()
    {
        // B-02: um usuário que envia muitas chaves descarta as próprias respostas, não as dos outros
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxEntries = 10, MaxEntriesPerScope = 2 });
        await CompleteAsync(store, "vitima", "v1");
        await CompleteAsync(store, "atacante", "a1");
        await CompleteAsync(store, "atacante", "a2");

        for (var i = 3; i < 50; i++)
            await CompleteAsync(store, "atacante", $"a{i}");

        await Assert.That(store.Count).IsEqualTo(3);
        await Assert.That((await BeginAsync(store, "vitima", "v1")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That((await BeginAsync(store, "atacante", "a49")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That((await BeginAsync(store, "atacante", "a1")).Status).IsNotEqualTo(IdempotencyBeginStatus.Completed);
    }

    [Test]
    public async Task Scope_quota_with_all_entries_in_progress_refuses_only_that_scope()
    {
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxEntries = 10, MaxEntriesPerScope = 2 });
        await BeginAsync(store, "atacante", "a1");
        await BeginAsync(store, "atacante", "a2");

        await Assert.That(async () => await BeginAsync(store, "atacante", "a3")).ThrowsExactly<InvalidOperationException>();
        await Assert.That((await BeginAsync(store, "outro", "o1")).Status).IsEqualTo(IdempotencyBeginStatus.Started);
    }

    [Test]
    public async Task Invalid_scope_quota_is_rejected()
    {
        await Assert.That(() => new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxEntriesPerScope = 0 }))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Default_key_is_rejected_by_the_store()
    {
        // B-05: default/new() do record struct não passam pelo construtor que valida
        var store = new InMemoryIdempotencyStore();
        IdempotencyKey key = default;

        await Assert.That(async () => await store.TryBeginAsync(new IdempotencyRequest(key, "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
            CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(async () => await store.CompleteAsync(key, Guid.NewGuid(), Response(), CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(async () => await store.AbandonAsync(new IdempotencyKey(), Guid.NewGuid(), CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(() => new IdempotencyRequest(key, "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)).Validate()).Throws<ArgumentException>();
    }

    [Test]
    public async Task Keys_and_scopes_are_compared_ordinally()
    {
        // B-04: o contrato define comparação ordinal (sensível a maiúsculas)
        var store = new InMemoryIdempotencyStore();

        var lower = await store.TryBeginAsync(new IdempotencyRequest(new("u", "pedido-1"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);
        var upper = await store.TryBeginAsync(new IdempotencyRequest(new("u", "Pedido-1"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);
        var otherScope = await store.TryBeginAsync(new IdempotencyRequest(new("U", "pedido-1"), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)), CancellationToken.None);

        await Assert.That(lower.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(upper.Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(otherScope.Status).IsEqualTo(IdempotencyBeginStatus.Started);
    }

    [Test]
    [Arguments("")]
    [Arguments("com\ncontrole")]
    public async Task Invalid_keys_are_rejected(string key)
    {
        await Assert.That(() => new IdempotencyKey("u", key)).ThrowsExactly<ArgumentException>();
    }

    private static IdempotencyRequest RequestFor(string scope, string key) =>
        new(new IdempotencyKey(scope, key), "h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

    private static IdempotentResponse ResponseOfSize(int bodyBytes) =>
        new(201, null, new Dictionary<string, string>(), new byte[bodyBytes]);

    private static async Task<bool> CompleteWithAsync(InMemoryIdempotencyStore store, string scope, string key, IdempotentResponse response)
    {
        var begin = await store.TryBeginAsync(RequestFor(scope, key), CancellationToken.None);
        return await store.CompleteAsync(new IdempotencyKey(scope, key), begin.LockId, response, CancellationToken.None);
    }

    [Test]
    public async Task Global_entry_limit_never_evicts_valid_responses_of_other_scopes()
    {
        // Antes, no limite total, a resposta válida de outro usuário era descartada e a repetição dele executava de novo
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxEntries = 2, MaxEntriesPerScope = 10 });
        await CompleteAsync(store, "vitima", "v1");
        await CompleteAsync(store, "atacante", "a1");

        // O atacante abre espaço com a própria resposta (a1), nunca com a da vítima
        await Assert.That((await BeginAsync(store, "atacante", "a2")).Status).IsEqualTo(IdempotencyBeginStatus.Started);

        // Um terceiro escopo sem nada próprio para descartar é recusado (fail closed), sem tocar em v1
        await Assert.That(async () => await BeginAsync(store, "outro", "o1")).ThrowsExactly<InvalidOperationException>();
        await Assert.That((await BeginAsync(store, "vitima", "v1")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That(store.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Global_entry_limit_purges_expired_entries_of_any_scope()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxEntries = 1 }, clock);
        await CompleteAsync(store, "vitima", "v1");

        clock.Advance(TimeSpan.FromHours(2));

        await Assert.That((await BeginAsync(store, "outro", "o1")).Status).IsEqualTo(IdempotencyBeginStatus.Started);
        await Assert.That(store.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Byte_budget_is_accounted_on_complete_and_released_on_removal()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var store = new InMemoryIdempotencyStore(timeProvider: clock);
        var response = Response("corpo");
        long expected = InMemoryIdempotencyStore.EstimateSize(response);

        var begin = await store.TryBeginAsync(Request(), CancellationToken.None);
        await Assert.That(store.TotalBytes).IsEqualTo(0L);
        await store.CompleteAsync(Key, begin.LockId, response, CancellationToken.None);
        await Assert.That(store.TotalBytes).IsEqualTo(expected);
        await Assert.That(expected).IsGreaterThan(5L);

        // Expirada e substituída por outra execução: os bytes saem da conta
        clock.Advance(TimeSpan.FromHours(2));
        await store.TryBeginAsync(Request("H2"), CancellationToken.None);
        await Assert.That(store.TotalBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task Byte_budget_evicts_only_the_requester_scope_and_refuses_otherwise()
    {
        long size = InMemoryIdempotencyStore.EstimateSize(ResponseOfSize(100));
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxTotalBytes = (2 * size) + (size / 2) });
        await Assert.That(await CompleteWithAsync(store, "vitima", "v1", ResponseOfSize(100))).IsTrue();
        await Assert.That(await CompleteWithAsync(store, "atacante", "a1", ResponseOfSize(100))).IsTrue();

        // O atacante não cabe: descarta a própria resposta mais antiga (a1), não a da vítima
        await Assert.That(await CompleteWithAsync(store, "atacante", "a2", ResponseOfSize(100))).IsTrue();
        await Assert.That((await BeginAsync(store, "vitima", "v1")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That((await BeginAsync(store, "atacante", "a2")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);

        // Escopo sem respostas próprias: a reserva é aceita (ainda há bytes livres), mas a resposta não cabe sem descartar
        // as dos outros, então é recusada e a reserva fica em andamento
        var begin = await store.TryBeginAsync(RequestFor("outro", "o1"), CancellationToken.None);
        await Assert.That(async () => await store.CompleteAsync(new IdempotencyKey("outro", "o1"), begin.LockId, ResponseOfSize(100),
            CancellationToken.None)).ThrowsExactly<InvalidOperationException>();
        await Assert.That((await BeginAsync(store, "outro", "o1")).Status).IsEqualTo(IdempotencyBeginStatus.InProgress);
        await Assert.That((await BeginAsync(store, "vitima", "v1")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);
        await Assert.That(store.TotalBytes).IsLessThanOrEqualTo((2 * size) + (size / 2));
    }

    [Test]
    public async Task Exhausted_byte_budget_refuses_new_reservations_before_execution()
    {
        long size = InMemoryIdempotencyStore.EstimateSize(ResponseOfSize(64));
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxTotalBytes = size });
        await Assert.That(await CompleteWithAsync(store, "vitima", "v1", ResponseOfSize(64))).IsTrue();

        var ex = await Assert.That(async () => await BeginAsync(store, "outro", "o1")).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("MaxTotalBytes");
        await Assert.That((await BeginAsync(store, "vitima", "v1")).Status).IsEqualTo(IdempotencyBeginStatus.Completed);
    }

    [Test]
    public async Task Response_larger_than_the_byte_budget_is_refused_and_the_reservation_stays_in_progress()
    {
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxTotalBytes = 10 });
        var begin = await store.TryBeginAsync(Request(), CancellationToken.None);

        await Assert.That(async () => await store.CompleteAsync(Key, begin.LockId, ResponseOfSize(100), CancellationToken.None))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(store.TotalBytes).IsEqualTo(0L);
        await Assert.That((await store.TryBeginAsync(Request(), CancellationToken.None)).Status).IsEqualTo(IdempotencyBeginStatus.InProgress);
    }

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    public async Task Invalid_byte_budget_is_rejected_at_startup(long maxTotalBytes)
    {
        await Assert.That(() => new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxTotalBytes = maxTotalBytes }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ServiceCollection().AddInMemoryIdempotencyStore(o => o.MaxTotalBytes = maxTotalBytes))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Concurrent_completions_never_exceed_the_byte_budget()
    {
        long size = InMemoryIdempotencyStore.EstimateSize(ResponseOfSize(256));
        long budget = size * 10;
        var store = new InMemoryIdempotencyStore(new InMemoryIdempotencyStoreOptions { MaxTotalBytes = budget, MaxEntries = 100_000 });

        await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(async () =>
        {
            try
            {
                await CompleteWithAsync(store, $"u{i % 8}", $"k{i}", ResponseOfSize(256));
            }
            catch (InvalidOperationException)
            {
                // Recusa sem espaço é o comportamento esperado sob pressão
            }
        })));

        await Assert.That(store.TotalBytes).IsLessThanOrEqualTo(budget);
        await Assert.That(store.TotalBytes % size).IsEqualTo(0L);
    }
}

public class IdempotencyMiddlewareTests
{
    private sealed class Endpoint
    {
        public int Executions;
        public TaskCompletionSource? Gate;
        public int Status = StatusCodes.Status201Created;
        public string Body = """{"id":1}""";
    }

    private static (RequestDelegate Pipeline, ServiceProvider Provider) Build(Endpoint endpoint, Action<IdempotencyOptions>? configure = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CallLog>();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>())
            .AddAspNetCore()
            .AddIdempotency(configure);
        extra?.Invoke(services);
        services.AddInMemoryIdempotencyStore();
        var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseTecIdempotency();
        app.Run(async context =>
        {
            Interlocked.Increment(ref endpoint.Executions);
            if (endpoint.Gate is { } gate)
                await gate.Task;
            context.Response.StatusCode = endpoint.Status;
            context.Response.ContentType = "application/json";
            context.Response.Headers.Location = "/pedidos/1";
            context.Response.Headers["X-Interno"] = "nao-guardar";
            await context.Response.WriteAsync(endpoint.Body);
        });

        return (app.Build(), provider);
    }

    /// <summary>Pipeline com o middleware e um endpoint próprio.</summary>
    private static (RequestDelegate Pipeline, ServiceProvider Provider) Build(RequestDelegate endpoint, Action<IdempotencyOptions>? configure = null,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CallLog>();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>())
            .AddAspNetCore()
            .AddIdempotency(configure);
        extra?.Invoke(services);
        services.AddInMemoryIdempotencyStore();
        var provider = services.BuildServiceProvider();

        var app = new ApplicationBuilder(provider);
        app.UseTecIdempotency();
        app.Run(endpoint);
        return (app.Build(), provider);
    }

    private static DefaultHttpContext Context(IServiceProvider provider, string? key, string body = """{"valor":10}""", string user = "u1",
        bool idempotent = true, bool keyRequired = false)
    {
        var context = new DefaultHttpContext { RequestServices = provider.CreateScope().ServiceProvider };
        context.Request.Method = "POST";
        context.Request.Path = "/pedidos";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentLength = context.Request.Body.Length;
        context.Response.Body = new MemoryStream();
        if (key is not null)
            context.Request.Headers["Idempotency-Key"] = key;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tec_uid", user)], "Teste"));
        var metadata = idempotent ? new EndpointMetadataCollection(new IdempotentAttribute { KeyRequired = keyRequired }) : EndpointMetadataCollection.Empty;
        context.SetEndpoint(new Microsoft.AspNetCore.Http.Endpoint(null, metadata, "teste"));
        return context;
    }

    private static string ResponseBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return new StreamReader(context.Response.Body).ReadToEnd();
    }

    private static string ErrorCode(HttpContext context) =>
        JsonDocument.Parse(ResponseBody(context)).RootElement.GetProperty("errors")[0].GetProperty("code").GetString()!;

    [Test]
    public async Task Same_key_and_body_replays_without_executing_again()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var first = Context(provider, "k1");
        await pipeline(first);
        var second = Context(provider, "k1");
        await pipeline(second);

        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(second.Response.StatusCode).IsEqualTo(201);
        await Assert.That(ResponseBody(second)).IsEqualTo("""{"id":1}""");
        await Assert.That(second.Response.Headers.Location.ToString()).IsEqualTo("/pedidos/1");
        await Assert.That(second.Response.Headers["Idempotent-Replayed"].ToString()).IsEqualTo("true");
        await Assert.That(second.Response.Headers.ContainsKey("X-Interno")).IsFalse();
        await Assert.That(first.Response.Headers.ContainsKey("Idempotent-Replayed")).IsFalse();
    }

    [Test]
    public async Task Concurrent_requests_with_same_key_execute_once()
    {
        var endpoint = new Endpoint { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var first = Context(provider, "k1");
        var running = pipeline(first);
        while (Volatile.Read(ref endpoint.Executions) == 0)
            await Task.Delay(5);

        var second = Context(provider, "k1");
        await pipeline(second);
        endpoint.Gate.SetResult();
        await running;

        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(second.Response.StatusCode).IsEqualTo(409);
        await Assert.That(ErrorCode(second)).IsEqualTo("IDEMPOTENCIA_EM_ANDAMENTO");
        await Assert.That(first.Response.StatusCode).IsEqualTo(201);
    }

    [Test]
    public async Task Many_parallel_requests_execute_the_endpoint_exactly_once()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var contexts = Enumerable.Range(0, 32).Select(_ => Context(provider, "paralela")).ToArray();
        await Task.WhenAll(contexts.Select(c => Task.Run(() => pipeline(c))));

        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(contexts.All(c => c.Response.StatusCode is 201 or 409)).IsTrue();
    }

    [Test]
    public async Task Same_key_with_other_body_returns_422()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        await pipeline(Context(provider, "k1"));
        var other = Context(provider, "k1", body: """{"valor":99}""");
        await pipeline(other);

        await Assert.That(other.Response.StatusCode).IsEqualTo(422);
        await Assert.That(ErrorCode(other)).IsEqualTo("IDEMPOTENCY_KEY_REUTILIZADA");
        await Assert.That(endpoint.Executions).IsEqualTo(1);
    }

    [Test]
    public async Task Keys_are_scoped_by_user()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        await pipeline(Context(provider, "k1", user: "u1"));
        var otherUser = Context(provider, "k1", user: "u2");
        await pipeline(otherUser);

        await Assert.That(endpoint.Executions).IsEqualTo(2);
        await Assert.That(otherUser.Response.Headers.ContainsKey("Idempotent-Replayed")).IsFalse();
    }

    [Test]
    public async Task Error_response_releases_the_key()
    {
        var endpoint = new Endpoint { Status = 400 };
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        await pipeline(Context(provider, "k1"));
        endpoint.Status = 201;
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(endpoint.Executions).IsEqualTo(2);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(201);
    }

    [Test]
    public async Task Exception_releases_the_key()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CallLog>();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()).AddAspNetCore().AddIdempotency();
        services.AddInMemoryIdempotencyStore();
        await using var provider = services.BuildServiceProvider();
        var calls = 0;
        var app = new ApplicationBuilder(provider);
        app.UseTecIdempotency();
        app.Run(context => ++calls == 1 ? throw new InvalidOperationException("falha") : context.Response.WriteAsync("ok"));
        var pipeline = app.Build();

        await Assert.That(async () => await pipeline(Context(provider, "k1"))).ThrowsExactly<InvalidOperationException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(ResponseBody(retry)).IsEqualTo("ok");
    }

    [Test]
    public async Task Client_disconnect_after_commit_does_not_release_the_key()
    {
        // A-01: o command é confirmado e o cliente desconecta antes de receber a resposta (RequestAborted cancelado);
        // a repetição com a mesma chave não pode executar a operação de novo
        var executions = 0;
        var (pipeline, provider) = Build(async context =>
        {
            Interlocked.Increment(ref executions);
            using var aborted = new CancellationTokenSource();
            context.RequestAborted = aborted.Token;
            await aborted.CancelAsync();
            await Result.Success(42).ToCreatedHttpResult(v => $"/pedidos/{v}").ExecuteAsync(context);
        });
        await using var _ = provider;

        try
        {
            await pipeline(Context(provider, "k1"));
        }
        catch (OperationCanceledException)
        {
            // Comportamento aceitável do servidor ao perder o cliente; o que importa é a chave
        }

        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(201);
        await Assert.That(retry.Response.Headers["Idempotent-Replayed"].ToString()).IsEqualTo("true");
        await Assert.That(retry.Response.Headers.Location.ToString()).IsEqualTo("/pedidos/42");
    }

    /// <summary>Simula a desconexão do cliente: cancela o <c>RequestAborted</c> e interrompe o endpoint.</summary>
    private static async Task AbortAsync(HttpContext context)
    {
        using var aborted = new CancellationTokenSource();
        context.RequestAborted = aborted.Token;
        await aborted.CancelAsync();
        context.RequestAborted.ThrowIfCancellationRequested();
    }

    private static void AddUnitOfWork(IServiceCollection services)
    {
        services.AddScoped<FakeUnitOfWork>();
        services.AddScoped<TEC.Cqrs.Persistence.IUnitOfWork>(sp => sp.GetRequiredService<FakeUnitOfWork>());
    }

    [Test]
    public async Task Cancellation_before_commit_releases_the_reservation_even_with_success_status()
    {
        // O status começa em 200 e o endpoint pode defini-lo antes de executar: sem commit e sem bytes escritos nada foi
        // efetivado, então a chave é liberada (antes, o status 2xx sozinho prendia a chave até o LockTimeout)
        var executions = 0;
        var (pipeline, provider) = Build(async context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                context.Response.StatusCode = StatusCodes.Status201Created;
                await AbortAsync(context);
            }

            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync("ok");
        });
        await using var _ = provider;

        await Assert.That(async () => await pipeline(Context(provider, "k1"))).Throws<OperationCanceledException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(executions).IsEqualTo(2);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(201);
        await Assert.That(ResponseBody(retry)).IsEqualTo("ok");
    }

    [Test]
    public async Task Cancellation_after_commit_keeps_the_reservation()
    {
        // O command foi confirmado pela transação do pipeline e o cliente desconecta antes da resposta: liberar a chave
        // deixaria a repetição executar a operação de novo
        var executions = 0;
        var (pipeline, provider) = Build(async context =>
        {
            Interlocked.Increment(ref executions);
            var result = await context.RequestServices.GetRequiredService<ISender>().Send(new IdempotentCommitCommand());
            if (result.IsSuccess)
                await AbortAsync(context);
        }, extra: AddUnitOfWork);
        await using var _ = provider;

        var first = Context(provider, "k1");
        await Assert.That(async () => await pipeline(first)).Throws<OperationCanceledException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(first.RequestServices.GetRequiredService<CallLog>().Entries).Contains("commit");
        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(409);
        await Assert.That(ErrorCode(retry)).IsEqualTo("IDEMPOTENCIA_EM_ANDAMENTO");
    }

    [Test]
    public async Task Exception_after_commit_keeps_the_reservation()
    {
        var executions = 0;
        var (pipeline, provider) = Build(async context =>
        {
            Interlocked.Increment(ref executions);
            await context.RequestServices.GetRequiredService<ISender>().Send(new IdempotentCommitCommand());
            throw new InvalidOperationException("falha depois do commit");
        }, extra: AddUnitOfWork);
        await using var _ = provider;

        await Assert.That(async () => await pipeline(Context(provider, "k1"))).ThrowsExactly<InvalidOperationException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task Cancellation_after_success_body_was_written_keeps_the_reservation()
    {
        // Sem transação do pipeline (ex.: endpoint que grava por conta própria): a resposta de sucesso já escrita é o sinal
        // de que a operação foi executada
        var executions = 0;
        var (pipeline, provider) = Build(async context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync("""{"id":7}""");
            await AbortAsync(context);
        });
        await using var _ = provider;

        await Assert.That(async () => await pipeline(Context(provider, "k1"))).Throws<OperationCanceledException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(409);
    }

    [Test]
    public async Task Cancellation_after_error_body_was_written_releases_the_reservation()
    {
        var executions = 0;
        var (pipeline, provider) = Build(async context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("""{"erro":true}""");
                await AbortAsync(context);
            }

            context.Response.StatusCode = StatusCodes.Status201Created;
            await context.Response.WriteAsync("ok");
        });
        await using var _ = provider;

        await Assert.That(async () => await pipeline(Context(provider, "k1"))).Throws<OperationCanceledException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(executions).IsEqualTo(2);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(201);
    }

    /// <summary>Store que falha ao guardar a resposta nas primeiras <paramref name="failures"/> chamadas.</summary>
    private sealed class FlakyCompleteStore(int failures) : IIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();
        private int _remaining = failures;
        private int _completeCalls;

        public int CompleteCalls => Volatile.Read(ref _completeCalls);

        public ValueTask<IdempotencyBeginResult> TryBeginAsync(IdempotencyRequest request, CancellationToken cancellationToken) =>
            _inner.TryBeginAsync(request, cancellationToken);

        public ValueTask<bool> CompleteAsync(IdempotencyKey key, Guid lockId, IdempotentResponse response, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _completeCalls);
            if (Interlocked.Decrement(ref _remaining) >= 0)
                throw new TimeoutException("banco indisponível: senha=segredo-do-banco");
            return _inner.CompleteAsync(key, lockId, response, cancellationToken);
        }

        public ValueTask<bool> AbandonAsync(IdempotencyKey key, Guid lockId, CancellationToken cancellationToken) =>
            _inner.AbandonAsync(key, lockId, cancellationToken);
    }

    [Test]
    public async Task Complete_failing_transiently_is_retried_and_the_response_is_stored()
    {
        var store = new FlakyCompleteStore(failures: 2);
        var logs = new ListLoggerProvider();
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, extra: s =>
        {
            s.AddSingleton<IIdempotencyStore>(store);
            s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
        });
        await using var _ = provider;

        var first = Context(provider, "k1");
        await pipeline(first);
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(store.CompleteCalls).IsEqualTo(3);
        await Assert.That(first.Response.StatusCode).IsEqualTo(201);
        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(retry.Response.Headers["Idempotent-Replayed"].ToString()).IsEqualTo("true");
        await Assert.That(logs.Entries.Count(e => e.EventId.Id == 1026)).IsEqualTo(2);
        await Assert.That(logs.Entries.Any(e => e.EventId.Id == 1027)).IsFalse();
    }

    [Test]
    public async Task Complete_failing_on_every_attempt_still_sends_the_response_and_keeps_the_reservation()
    {
        // A operação já foi executada: o cliente recebe a resposta, a chave fica reservada (409 na repetição, sem executar
        // de novo) e o erro é registrado sem a chave, o corpo ou a mensagem da exceção do store
        var store = new FlakyCompleteStore(failures: int.MaxValue);
        var logs = new ListLoggerProvider();
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, extra: s =>
        {
            s.AddSingleton<IIdempotencyStore>(store);
            s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
        });
        await using var _ = provider;

        var first = Context(provider, "chave-secreta-1");
        await pipeline(first);
        var retry = Context(provider, "chave-secreta-1");
        await pipeline(retry);

        // Limitado: a primeira tentativa mais uma por espera (3 no total), sem insistir indefinidamente
        await Assert.That(store.CompleteCalls).IsEqualTo(IdempotencyMiddleware.CompleteRetryDelays.Length + 1);
        await Assert.That(store.CompleteCalls).IsEqualTo(3);
        await Assert.That(first.Response.StatusCode).IsEqualTo(201);
        await Assert.That(ResponseBody(first)).IsEqualTo("""{"id":1}""");
        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(retry.Response.StatusCode).IsEqualTo(409);

        var error = logs.Entries.Single(e => e.EventId.Id == 1027);
        await Assert.That(error.Level).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Error);
        await Assert.That(error.Exception).IsNull();
        await Assert.That(error.Message).Contains(typeof(TimeoutException).FullName!);
        foreach (var entry in logs.Entries)
        {
            await Assert.That(entry.Message).DoesNotContain("segredo");
            await Assert.That(entry.Message).DoesNotContain("chave-secreta-1");
            await Assert.That(entry.Message).DoesNotContain("\"id\"");
        }
    }

    [Test]
    public async Task Response_is_stored_before_being_sent_to_the_client()
    {
        // A-01: falha ao enviar ao cliente (conexão caída) depois do 2xx não libera a chave: a resposta já está guardada
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var first = Context(provider, "k1");
        first.Response.Body = new BrokenStream();
        await Assert.That(async () => await pipeline(first)).Throws<IOException>();
        var retry = Context(provider, "k1");
        await pipeline(retry);

        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(ResponseBody(retry)).IsEqualTo("""{"id":1}""");
        await Assert.That(retry.Response.Headers["Idempotent-Replayed"].ToString()).IsEqualTo("true");
    }

    /// <summary>Conexão caída: toda escrita falha.</summary>
    private sealed class BrokenStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("conexão encerrada");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("conexão encerrada"));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("conexão encerrada"));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "Teste"));

    [Test]
    public async Task Same_user_id_value_from_other_claim_type_or_issuer_is_another_scope()
    {
        // M-02: valores iguais de esquemas de autenticação diferentes (tipo de claim ou emissor) não compartilham as chaves
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var uid = Context(provider, "k1");
        uid.User = Principal(new Claim("tec_uid", "1", ClaimValueTypes.String, "emissor-a"));
        var otherIssuer = Context(provider, "k1");
        otherIssuer.User = Principal(new Claim("tec_uid", "1", ClaimValueTypes.String, "emissor-b"));
        var otherType = Context(provider, "k1");
        otherType.User = Principal(new Claim(ClaimTypes.NameIdentifier, "1", ClaimValueTypes.String, "emissor-a"));
        var same = Context(provider, "k1");
        same.User = Principal(new Claim("tec_uid", "1", ClaimValueTypes.String, "emissor-a"));

        foreach (var context in new[] { uid, otherIssuer, otherType, same })
            await pipeline(context);

        await Assert.That(endpoint.Executions).IsEqualTo(3);
        await Assert.That(same.Response.Headers["Idempotent-Replayed"].ToString()).IsEqualTo("true");
    }

    [Test]
    public async Task User_id_from_unauthenticated_identity_is_ignored()
    {
        // O id do usuário vem só de identidades autenticadas (uma identidade anexada sem autenticação não define o escopo)
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var context = Context(provider, "k1", keyRequired: true);
        context.User = new ClaimsPrincipal([
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "maria")], "Teste"),
            new ClaimsIdentity([new Claim("tec_uid", "outro-usuario")])]);
        await pipeline(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(500);
        await Assert.That(endpoint.Executions).IsEqualTo(0);
    }

    [Test]
    public async Task ScopeDiscriminator_separates_tenants_of_the_same_user()
    {
        // M-02: o mesmo usuário com a mesma chave em outro tenant não recebe a resposta guardada do primeiro
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, o => o.ScopeDiscriminator = c => c.Request.Headers["X-Tenant"].ToString());
        await using var _ = provider;

        var tenantA = Context(provider, "k1");
        tenantA.Request.Headers["X-Tenant"] = "a";
        var tenantB = Context(provider, "k1");
        tenantB.Request.Headers["X-Tenant"] = "b";
        var tenantARetry = Context(provider, "k1");
        tenantARetry.Request.Headers["X-Tenant"] = "a";

        await pipeline(tenantA);
        await pipeline(tenantB);
        await pipeline(tenantARetry);

        await Assert.That(endpoint.Executions).IsEqualTo(2);
        await Assert.That(tenantB.Response.Headers.ContainsKey("Idempotent-Replayed")).IsFalse();
        await Assert.That(tenantARetry.Response.Headers["Idempotent-Replayed"].ToString()).IsEqualTo("true");
    }

    [Test]
    public async Task HashedRequestHeaders_are_part_of_the_request_content()
    {
        // M-02: com o cabeçalho no hash, a mesma chave com outro valor dele é conteúdo diferente (422), não repetição
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, o => o.HashedRequestHeaders.Add("X-Tenant"));
        await using var _ = provider;

        var first = Context(provider, "k1");
        first.Request.Headers["X-Tenant"] = "a";
        var other = Context(provider, "k1");
        other.Request.Headers["X-Tenant"] = "b";
        var missing = Context(provider, "k1");

        await pipeline(first);
        await pipeline(other);
        await pipeline(missing);

        await Assert.That(endpoint.Executions).IsEqualTo(1);
        await Assert.That(other.Response.StatusCode).IsEqualTo(422);
        await Assert.That(missing.Response.StatusCode).IsEqualTo(422);
    }

    [Test]
    public async Task Required_key_with_anonymous_user_returns_401_without_executing()
    {
        // M-03: com keyRequired, executar sem escopo seria executar sem a proteção exigida
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var context = Context(provider, "k1", keyRequired: true);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        await pipeline(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(401);
        await Assert.That(endpoint.Executions).IsEqualTo(0);
    }

    [Test]
    public async Task Required_key_with_user_without_id_claim_returns_500_without_executing()
    {
        // M-03: usuário autenticado sem nenhum dos UserIdClaimTypes (ex.: API key) é erro de configuração, não passagem livre
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var required = Context(provider, "k1", keyRequired: true);
        required.User = Principal(new Claim(ClaimTypes.Name, "servico"));
        var optional = Context(provider, "k1");
        optional.User = Principal(new Claim(ClaimTypes.Name, "servico"));
        await pipeline(required);
        await pipeline(optional);

        await Assert.That(required.Response.StatusCode).IsEqualTo(500);
        await Assert.That(ErrorCode(required)).IsEqualTo("IDEMPOTENCIA_SEM_ESCOPO");
        await Assert.That(optional.Response.StatusCode).IsEqualTo(201);
        await Assert.That(endpoint.Executions).IsEqualTo(1);
    }

    [Test]
    public async Task Header_with_several_values_and_empty_first_is_invalid()
    {
        // B-07: "Idempotency-Key: , k1" não pode ser tratado como ausente
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var optional = Context(provider, null);
        optional.Request.Headers["Idempotency-Key"] = new Microsoft.Extensions.Primitives.StringValues(["", "k1"]);
        var required = Context(provider, null, keyRequired: true);
        required.Request.Headers["Idempotency-Key"] = new Microsoft.Extensions.Primitives.StringValues(["", "k1"]);
        await pipeline(optional);
        await pipeline(required);

        await Assert.That(optional.Response.StatusCode).IsEqualTo(400);
        await Assert.That(ErrorCode(optional)).IsEqualTo("IDEMPOTENCY_KEY_INVALIDA");
        await Assert.That(ErrorCode(required)).IsEqualTo("IDEMPOTENCY_KEY_INVALIDA");
        await Assert.That(endpoint.Executions).IsEqualTo(0);
    }

    [Test]
    public async Task Missing_key_passes_through_unless_required()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        var optional = Context(provider, null);
        await pipeline(optional);
        var required = Context(provider, null, keyRequired: true);
        await pipeline(required);

        await Assert.That(optional.Response.StatusCode).IsEqualTo(201);
        await Assert.That(required.Response.StatusCode).IsEqualTo(400);
        await Assert.That(ErrorCode(required)).IsEqualTo("IDEMPOTENCY_KEY_OBRIGATORIA");
    }

    [Test]
    public async Task Invalid_key_returns_400()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, o => o.MaxKeyLength = 5);
        await using var _ = provider;

        var context = Context(provider, "chave-longa");
        await pipeline(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(400);
        await Assert.That(ErrorCode(context)).IsEqualTo("IDEMPOTENCY_KEY_INVALIDA");
        await Assert.That(endpoint.Executions).IsEqualTo(0);
    }

    [Test]
    public async Task Body_over_limit_returns_413_without_executing()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, o => o.MaxRequestBodyBytes = 4);
        await using var _ = provider;

        var context = Context(provider, "k1");
        await pipeline(context);

        await Assert.That(context.Response.StatusCode).IsEqualTo(413);
        await Assert.That(endpoint.Executions).IsEqualTo(0);
    }

    [Test]
    public async Task Response_over_limit_is_sent_but_not_stored()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint, o => o.MaxResponseBodyBytes = 3);
        await using var _ = provider;

        var first = Context(provider, "k1");
        await pipeline(first);
        await pipeline(Context(provider, "k1"));

        await Assert.That(ResponseBody(first)).IsEqualTo("""{"id":1}""");
        await Assert.That(endpoint.Executions).IsEqualTo(2);
    }

    [Test]
    public async Task Endpoints_without_metadata_and_anonymous_requests_are_not_tracked()
    {
        var endpoint = new Endpoint();
        var (pipeline, provider) = Build(endpoint);
        await using var _ = provider;

        await pipeline(Context(provider, "k1", idempotent: false));
        await pipeline(Context(provider, "k1", idempotent: false));
        var anonymous = Context(provider, "k2");
        anonymous.User = new ClaimsPrincipal(new ClaimsIdentity());
        await pipeline(anonymous);

        await Assert.That(endpoint.Executions).IsEqualTo(3);
    }

    [Test]
    public async Task Invalid_options_fail_at_startup()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()).AddAspNetCore().AddIdempotency(o => o.Retention = TimeSpan.Zero);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IOptions<IdempotencyOptions>>().Value).ThrowsExactly<OptionsValidationException>();
    }

    [Test]
    public async Task UseTecIdempotency_without_store_fails()
    {
        var services = new ServiceCollection();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()).AddAspNetCore().AddIdempotency();
        await using var provider = services.BuildServiceProvider();

        var ex = await Assert.That(() => new ApplicationBuilder(provider).UseTecIdempotency()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ex!.Message).Contains("IIdempotencyStore");
    }

    [Test]
    public async Task AddIdempotency_twice_fails()
    {
        var builder = new ServiceCollection().AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>()).AddIdempotency();

        await Assert.That(() => builder.AddIdempotency()).ThrowsExactly<InvalidOperationException>();
    }
}

/// <summary>Command confirmado pela transação do pipeline (com o FakeUnitOfWork), para os testes de cancelamento após o commit.</summary>
[TEC.Cqrs.Authorization.AllowAnonymousRequest, TEC.Cqrs.Validation.SkipValidation]
public sealed record IdempotentCommitCommand : ICommand;

internal sealed class IdempotentCommitHandler : ICommandHandler<IdempotentCommitCommand>
{
    public Task<Result> Handle(IdempotentCommitCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}
