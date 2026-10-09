using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    [Test]
    [Arguments("")]
    [Arguments("com\ncontrole")]
    public async Task Invalid_keys_are_rejected(string key)
    {
        await Assert.That(() => new IdempotencyKey("u", key)).ThrowsExactly<ArgumentException>();
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

    private static (RequestDelegate Pipeline, ServiceProvider Provider) Build(Endpoint endpoint, Action<IdempotencyOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<CallLog>();
        services.AddTecCqrs(o => o.RegisterServicesFromAssemblyContaining<CallLog>())
            .AddAspNetCore()
            .AddIdempotency(configure);
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
