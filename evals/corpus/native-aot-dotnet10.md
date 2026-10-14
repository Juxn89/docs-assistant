# Native AOT in .NET 10: Ship a Minimal API That Skips the JIT Tax

# Native AOT in .NET 10: Ship a Minimal API That Skips the JIT Tax

Every time a container-orchestrated .NET service scales out, it pays the same toll: the runtime starts, the JIT compiler warms up, and only *then* does the app start serving traffic. Multiply that by however many replicas your autoscaler spins up during a traffic spike, and "a few hundred milliseconds of startup" turns into real money and real latency on your P99. Native AOT exists to skip that toll entirely — no JIT, no warm-up, just native machine code that starts running immediately.

This one's a deliberate change of pace. The last four posts here built Claude into .NET one way or another; this one is plain .NET — no LLM in sight, just a Minimal API that starts fast because it never asked a JIT compiler for permission.



## What Native AOT actually is

"AOT" stands for **ahead-of-time** compilation — the opposite of the JIT ("just-in-time") compilation .NET normally does. With Native AOT, `dotnet publish` runs a native IL compiler that turns your entire app, plus the runtime bits it actually uses, into **one native executable** at publish time. There's no IL sitting around waiting to be JIT-compiled at startup, because there's no JIT compiler in the deployed app at all.

Three consequences fall out of that:

1. **Startup is native-code-fast**, because the CPU is running machine code from the first instruction — nothing needs to be compiled first.
2. **The binary is self-contained and trimmed** — it ships only the code your app's dependency graph actually reaches, not the whole framework.
3. **Some dynamic behaviors stop working** — anything that depends on generating or loading code at runtime (reflection-heavy serialization, `Assembly.LoadFile`, runtime code-gen) has nothing left to generate against. We'll come back to this; it's the main thing that decides whether AOT fits your service.

Microsoft runs its own benchmark comparing a Native AOT app, a trimmed-but-JIT app, and a plain untrimmed app across disk size, memory, and startup time — Native AOT wins on all three in that comparison. Rather than repeat numbers I can't verify against your hardware and workload, the "Real-world numbers" section below shows you exactly how to produce your own.



---

## Building a real service: Aurora Coffee Co.'s Orders API

Same fictional shop from the Claude posts, minus Claude this time. Aurora Coffee Co. needs an Orders API — small, focused, and exactly the shape of service that gets deployed a hundred times across a fleet. Scaffold it with the official AOT-ready template:

```bash
dotnet new webapiaot -o AuroraCoffee.Orders
cd AuroraCoffee.Orders
```

`webapiaot` (Web API Native AOT) isn't the regular Web API template with a flag flipped — it's shaped differently on purpose:

- It calls `WebApplication.CreateSlimBuilder()` instead of `CreateBuilder()`, which registers only the essential ASP.NET Core features (no HTTPS/HTTP3 in Kestrel, no IIS integration) to keep the trimmed output small.
- Minimal APIs only — MVC isn't AOT-compatible, so there are no controllers to reach for.
- JSON serialization is wired to a source-generated `JsonSerializerContext` instead of reflection, because reflection-based `System.Text.Json` doesn't work once the type metadata it needs has been trimmed away.

### The domain and the data

One deliberate choice up front: **no Entity Framework Core**. EF Core isn't AOT-compatible today — its query pipeline leans on runtime code generation that Native AOT can't produce. For a real service you'd reach for Dapper or raw ADO.NET instead; for this example, an injectable in-memory store makes the same point without a database dependency:

```csharp
using System.Collections.Concurrent;

public sealed record Order(string Id, string Sku, int Quantity, string Status);

public sealed class OrdersStore
{
    // Registered as a singleton and written from concurrent requests, so a plain
    // Dictionary would be a data race — it can corrupt its buckets during a resize.
    private readonly ConcurrentDictionary<string, Order> _orders = new()
    {
        ["A-2001"] = new("A-2001", "ETH-250", 2, "processing"),
        ["A-2002"] = new("A-2002", "COL-1KG", 1, "shipped"),
    };

    // Starts past the seeded IDs so the first generated order is A-2003.
    private int _nextOrderId = 2002;

    public Order? Find(string id) => _orders.TryGetValue(id, out var order) ? order : null;

    public Order Place(string sku, int quantity)
    {
        var id = Interlocked.Increment(ref _nextOrderId);
        var order = new Order($"A-{id}", sku, quantity, "processing");
        _orders[order.Id] = order;
        return order;
    }
}
```

### Program.cs — the AOT-shaped parts

```csharp
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddSingleton<OrdersStore>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

var orders = app.MapGroup("/orders");

orders.MapGet("/{id}", (string id, OrdersStore store) =>
    store.Find(id) is { } order ? Results.Ok(order) : Results.NotFound());

orders.MapPost("/", (PlaceOrderRequest request, OrdersStore store) =>
{
    var order = store.Place(request.Sku, request.Quantity);
    return Results.Created($"/orders/{order.Id}", order);
});

app.Run();

public sealed record PlaceOrderRequest(string Sku, int Quantity);

[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(PlaceOrderRequest))]
internal partial class AppJsonContext : JsonSerializerContext;
```

That `AppJsonContext` is the part every Native AOT Minimal API needs and every tutorial that skips it will leave you debugging a runtime exception. Every type that crosses the HTTP body — request or response — needs a `[JsonSerializable]` entry here. Miss one, and instead of a compile error you get a runtime failure the first time that type needs serializing, because the reflection-based fallback simply isn't there anymore.

Publishing it is the same command as any self-contained app, since the `webapiaot` template already set `<PublishAot>true</PublishAot>` in the `.csproj`:

```bash
dotnet publish -r linux-x64 -c Release
```

Watch the build output on this step — every AOT or trimming warning it prints is a real bug report, not noise. An app that publishes with zero warnings behaves identically to the JIT-compiled version; one that publishes *with* warnings might throw at runtime on a code path your tests didn't exercise. Fix them before you ship, not after a page.



---

## Real-world numbers: measure it yourself

Rather than quote a benchmark that was run on hardware you don't have, publish all three variants of the same app and compare them on yours:

```bash
# 1. Framework-dependent (needs the .NET runtime installed on the target machine)
dotnet publish -c Release -o out/framework-dependent

# 2. Self-contained + trimmed, still JIT-compiled
dotnet publish -r linux-x64 -c Release --self-contained -p:PublishTrimmed=true -o out/trimmed

# 3. Native AOT
dotnet publish -r linux-x64 -c Release -o out/aot
```

Then compare what actually matters for a deployed service:

- **Disk size**: `du -sh out/*/AuroraCoffee.Orders*` (or the container image size, if you're shipping one) — this is what you pull across the network on every deploy.
- **Startup time**: time the gap between process start and the first successful request, e.g. `time curl --retry 20 --retry-connrefused http://localhost:5000/orders/A-2001` right after launching each variant.
- **Idle memory**: `ps -o rss -p <pid>` (Linux) or Task Manager's working set (Windows), a few seconds after startup with no traffic yet.

Microsoft's own template benchmark (size, memory, and startup time compared across AOT, trimmed, and untrimmed) shows Native AOT ahead on all three — see the chart in the [ASP.NET Core Native AOT docs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot). Your numbers will differ by workload and hardware, but the *direction* — AOT smallest and fastest, untrimmed largest and slowest — should hold.

---

## What's actually new in .NET 10 (not carried over from .NET 7/8)

Native AOT itself shipped in .NET 7 and matured through .NET 8 and 9. .NET 10 adds three things worth knowing about specifically:

**`IsAotCompatible` assembly metadata.** Library authors can now declare AOT compatibility explicitly:

```xml
<PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
</PropertyGroup>
```

Setting it turns on trim, single-file, and AOT analyzers for that library automatically. Pair it with `VerifyReferenceAotCompatibility` in your own app to get warned when a *dependency* hasn't made that promise:

```xml
<PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
    <VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
</PropertyGroup>
```

**File-based apps target AOT by default.** A single `.cs` file run with `dotnet run app.cs` can now be published straight to a native executable with `dotnet publish app.cs` — no project file needed. .NET 10 makes AOT the default for these; opt out per-file if a script needs an AOT-incompatible package:

```csharp
#:property PublishAot=false
```

**Console apps get native container images for free.** `dotnet publish /t:PublishContainer` now works on any console app, not just ASP.NET Core and Worker Service projects — no `<EnableSdkContainerSupport>` opt-in required anymore. Combine it with Native AOT and a worker or CLI tool goes straight from source to a minimal container image in one command.



---

## When to use it — and when to leave it alone

Reach for Native AOT when you're running **many instances of the same service** — the kind of workload where shaving startup time and memory off *one* replica pays for itself a hundredfold across a fleet: containerized microservices, serverless functions, anything an autoscaler spins up and down under load.

Leave it alone when your stack leans on the dynamic features it can't do: **EF Core** (not AOT-compatible today), reflection-heavy libraries that haven't added source generators, MVC, Blazor Server, full SignalR, or Session — all either unsupported or only partially supported under Native AOT as of .NET 10. And remember AOT binaries are **platform-specific**: a `linux-x64` publish only runs on `linux-x64`, so cross-platform framework-dependent deployment stays simpler if you support many OS/architecture combinations from one build.

**Rule of thumb: Native AOT when instance count is high and your dependencies are AOT-clean; stick with JIT (optionally trimmed) when EF Core or reflection-heavy libraries are load-bearing.** And if a library vendor tells you their package "works fine with reflection," that's the moment to remember Native AOT doesn't do favors — it just doesn't run code that was never there to begin with.

---

## Key Takeaways

- **AOT trades dynamism for speed** — no JIT means no runtime compilation step, but also no unbounded reflection, no runtime code-gen, no dynamic assembly loading. Know which of those your app actually needs before committing.
- **`CreateSlimBuilder` + source-generated JSON is the shape of an AOT Minimal API** — `dotnet new webapiaot` scaffolds both correctly; skipping the `JsonSerializerContext` is the single most common way to break one.
- **EF Core is the biggest real-world blocker** — if your service is EF-heavy, Native AOT isn't a drop-in win yet; Dapper or ADO.NET are the AOT-compatible alternatives today.
- **Trust the publish warnings, not your gut** — zero AOT/trim warnings at publish time means the AOT build behaves like the JIT build; any warning is a potential runtime failure waiting for the right input.
- **.NET 10 extends AOT past web services** — `IsAotCompatible` metadata for library authors, AOT-by-default file-based apps, and native container images for any console app are new this release, not just Native AOT 101 repeated for the fourth year running.
