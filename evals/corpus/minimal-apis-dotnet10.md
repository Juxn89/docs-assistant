# Minimal APIs Done Right in .NET 10: Validation, Versioning, and OpenAPI Without Controllers

# Minimal APIs Done Right in .NET 10: Validation, Versioning, and OpenAPI Without Controllers

Last time we [published Aurora Coffee Co.'s Orders API with Native AOT](https://dev.to/jgomezdev/native-aot-in-net-10-ship-a-minimal-api-that-skips-the-jit-tax-4i9b) and made it start in a blink. What we did *not* do is make it a service anyone should depend on. It will cheerfully accept an order for negative five bags of coffee. It has no version in its URL, so the first breaking change you ship breaks every client at once. And it tells an integrating team exactly nothing about its own shape — "read the source, I guess."

That's fine for a startup-time benchmark. It's a liability in production. This post closes the gap: validation, versioning, and OpenAPI on a Minimal API, no controllers involved. The good news is that .NET 10 finally ships a first-party answer for the hardest of the three.


![Image description](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/d7nkh6ph5s1lpnr9k8u3.jpg)

## Where we left off

The AOT post ended with two endpoints and a singleton store:

```csharp
var orders = app.MapGroup("/orders");

orders.MapGet("/{id}", (string id, OrdersStore store) =>
    store.Find(id) is { } order ? Results.Ok(order) : Results.NotFound());

orders.MapPost("/", (PlaceOrderRequest request, OrdersStore store) =>
{
    var order = store.Place(request.Sku, request.Quantity);
    return Results.Created($"/orders/{order.Id}", order);
});
```

Three gaps, in the order they'll hurt you:

1. **Nothing validates the request.** `{"sku": "", "quantity": -5}` becomes a real order.
2. **Nothing versions the contract.** Rename one JSON field and every client breaks simultaneously.
3. **Nothing describes the API.** No schema, no docs, no generated client.

We'll fix them in that order.

## Step 1: Groups are the seam everything else hooks into

`MapGroup` looks like a convenience for sharing a URL prefix. It's actually the extension point that validation, filters, versioning, and OpenAPI metadata all attach to — apply a convention to the group and every endpoint inside inherits it.

Keep the handlers out of `Program.cs`. Named methods in a static class beat lambdas for three reasons: you can unit-test them, the compiler can infer richer OpenAPI metadata from their signatures, and — new in .NET 10 — their XML doc comments flow into the OpenAPI document.

```csharp
internal static class OrderEndpoints
{
    /// <summary>Looks up a single order by its identifier.</summary>
    /// <param name="id">The order identifier, for example <c>A-2001</c>.</param>
    public static Results<Ok<OrderV1Response>, NotFound> GetOrderV1(string id, OrdersStore store)
        => store.Find(id) is { } order
            ? TypedResults.Ok(new OrderV1Response(order.Id, order.Sku, order.Quantity, order.Status))
            : TypedResults.NotFound();
}
```

Two things worth pausing on. `Results<Ok<T>, NotFound>` is a discriminated union: the compiler rejects any return that isn't one of the listed types, and OpenAPI reads both the 200 and the 404 straight off the signature — no `.Produces<T>()` needed. And the handler returns `OrderV1Response`, not the domain `Order`. That separation looks like ceremony until Step 3, where it's the entire reason versioning is survivable.

XML comments only reach the document if you enable the docs file:

```xml
<PropertyGroup>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
</PropertyGroup>
```

## Step 2: Validation, finally built in

Before .NET 10 the answer was FluentValidation or a hand-rolled filter. Now `System.ComponentModel.DataAnnotations` works on Minimal API parameters directly, wired up by a source generator:

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
```

That's the whole setup. `Microsoft.Extensions.Validation` lives in the ASP.NET Core shared framework, so a `Microsoft.NET.Sdk.Web` project needs no `PackageReference` — a plain class library does. And no, you don't need the `InterceptorsNamespaces` csproj incantation you'll find in preview-era blog posts; the SDK adds it for you on `net10.0`.

Now annotate the request:

```csharp
public sealed record PlaceOrderRequest(
    [property: Required]
    [property: RegularExpression(
        @"^[A-Z]{3}-[A-Z0-9]{2,4}$",
        ErrorMessage = "SKU must look like ETH-250 or COL-1KG.")]
    string Sku,

    [property: Range(1, 100, ErrorMessage = "Orders are capped at 100 units per line.")]
    int Quantity);
```

**Do not skip the `[property:]` prefix.** On a positional record, an attribute that's legal on both a parameter and a property binds to the *parameter* by default, and the validator reads properties. Leave it off and your validation silently does nothing — the worst possible failure mode, since the endpoint keeps returning 201 and you only find out from a support ticket.

A failed request now gets a `400` with `application/problem+json`, RFC 9457 shaped:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Sku": ["SKU must look like ETH-250 or COL-1KG."],
    "Quantity": ["Orders are capped at 100 units per line."]
  }
}
```

Registering `AddProblemDetails()` is what lets you shape that payload globally instead of accepting the default.

Three things to know before you rely on it:

- **Opt out with `.DisableValidation()`.** It's generic over `IEndpointConventionBuilder`, so it works on one endpoint or an entire legacy group.
- **The generator only sees the assembly where `AddValidation()` is called.** Split your DTOs into another project and it finds nothing — expose a small `AddMyModuleValidation()` extension per assembly.
- **Nullable value-type parameters are skipped in .NET 10.** A `[Range]` on an `int?` parameter is silently ignored ([dotnet/aspnetcore#67033](https://github.com/dotnet/aspnetcore/issues/67033)); it's fixed in .NET 11. Until then, take a non-nullable parameter or validate it yourself.


![Image description](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/nng3jfo9ypzt0iz0ev1f.jpg)

## Step 3: The rules DataAnnotations can't reach

DataAnnotations validate *shape*: is this string present, is this number in range. They can't answer "do we actually have 40 bags of ETH-250 in the warehouse," because that needs a service. That's what `IEndpointFilter` is for.

```csharp
public sealed class StockReservationFilter(InventoryStore inventory) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.GetArgument<PlaceOrderRequest>(0);

        if (!inventory.TryReserve(request.Sku, request.Quantity))
        {
            return TypedResults.Problem(
                title: "Insufficient stock",
                detail: $"Not enough {request.Sku} on hand to fill {request.Quantity} units.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return await next(context);
    }
}
```

The filter *reserves* rather than *checks*. A `HasStock()` call followed by a decrement in the handler is a textbook check-then-act race: two requests both see 12 bags, both reserve 10, and you've sold 20 bags you don't have. The reservation has to be atomic, which means a compare-and-swap loop:

```csharp
using System.Collections.Concurrent;

public sealed class InventoryStore
{
    private readonly ConcurrentDictionary<string, int> _stock = new()
    {
        ["ETH-250"] = 40,
        ["COL-1KG"] = 12,
    };

    public bool TryReserve(string sku, int quantity)
    {
        // Guard the invariant here, not in the caller. A store that a bad caller can
        // drive negative is a bug even when today's only caller is well-behaved.
        if (quantity <= 0) return false;

        while (_stock.TryGetValue(sku, out var available))
        {
            if (available < quantity) return false;

            // Only succeeds if nobody changed the count since we read it.
            if (_stock.TryUpdate(sku, available - quantity, available)) return true;
        }

        return false;
    }
}
```

Register it per endpoint, not per group — a `GET` has no `PlaceOrderRequest` to pull out of argument zero:

```csharp
v1.MapPost("/", OrderEndpoints.PlaceOrder).AddEndpointFilter<StockReservationFilter>();
```

Filter ordering is worth memorizing, because it's asymmetric: code *before* `await next(...)` runs first-added-first, code *after* it runs first-added-**last**, and group filters always wrap endpoint filters regardless of the order you configured the groups in.

One trap: `AddEndpointFilter<T>()` builds the filter **once, when the endpoint is built** — it's effectively a singleton. Constructor-injecting a singleton store is fine; constructor-injecting a `DbContext` or anything else scoped is a captive dependency that will hand you the same stale instance for the life of the process. Resolve scoped services inside `InvokeAsync` from `context.HttpContext.RequestServices` instead.

## Step 4: Versioning, and versioning only what broke

Add the two community packages that Microsoft's own guidance points at:

```bash
dotnet add package Asp.Versioning.Http
dotnet add package Asp.Versioning.OpenApi
```

```csharp
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader(),
        new HeaderApiVersionReader("X-Api-Version"));
})
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";        // v1, v1.1, v2
    options.SubstituteApiVersionInUrl = true;  // required for URL-segment versioning
})
.AddOpenApi();
```

`ReportApiVersions` makes every response carry `api-supported-versions` and `api-deprecated-versions` headers, which is how a client discovers a deprecation without reading your changelog. `ApiVersionReader.Combine` accepts the version from the URL segment *or* a header, so a client that can't change its URL structure still has a path forward.

Then declare the versions:

```csharp
var orders = app.NewVersionedApi("Orders");

var v1 = orders.MapGroup("/api/v{version:apiVersion}/orders")
               .HasApiVersion(1.0)
               .WithTags("Orders");

var v2 = orders.MapGroup("/api/v{version:apiVersion}/orders")
               .HasApiVersion(2.0)
               .WithTags("Orders");
```

Note `HasApiVersion(1.0)` — a `double`, not a string. There is no `string` overload, despite what a few samples floating around suggest.

Here's the part most versioning tutorials get wrong: **you version the endpoints that broke, not the whole API.** Aurora's v2 renames `status` to `state`, adds `placedAt`, and introduces a list endpoint. The `POST` didn't change at all:

```csharp
v1.MapGet("/{id}", OrderEndpoints.GetOrderV1);

v2.MapGet("/{id}", OrderEndpoints.GetOrderV2);
v2.MapGet("/", OrderEndpoints.ListOrders);

v1.MapPost("/", OrderEndpoints.PlaceOrder).AddEndpointFilter<StockReservationFilter>();
v2.MapPost("/", OrderEndpoints.PlaceOrder).AddEndpointFilter<StockReservationFilter>();
```

That single shared `PlaceOrder` handler only works because it returns `201 Created` with a `Location` header and no body — there's no versioned response shape to disagree about:

```csharp
/// <summary>Places a new order and returns its location.</summary>
public static Created PlaceOrder(PlaceOrderRequest request, OrdersStore store, HttpContext http)
{
    var order = store.Place(request.Sku, request.Quantity);
    return TypedResults.Created($"{http.Request.Path}/{order.Id}");
}
```

Because the location is built from the incoming path, a client that posted to `/api/v2/orders` gets pointed back at `/api/v2/orders/A-2003`. The response body is where versions diverge, and this endpoint doesn't have one.


![Image description](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/zrb5ip25vwa9kpem2pdy.jpg)

## Step 5: OpenAPI without Swashbuckle

`Microsoft.AspNetCore.OpenApi` replaced Swashbuckle as the default in .NET 9, and .NET 10 sharpened it: **OpenAPI 3.1 is now the default document version**, YAML output works at runtime, and you can transform a single operation without touching the whole document.

Because we're versioned, the per-version documents come from `Asp.Versioning.OpenApi`:

```csharp
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();   // /openapi/v1.json and /openapi/v2.json
    app.MapScalarApiReference();                 // interactive UI at /scalar
}
```

Without versioning it's just `builder.Services.AddOpenApi()` plus `app.MapOpenApi()`, serving `/openapi/v1.json`.

Four things that will save you an afternoon:

- **No UI ships in the box.** ASP.NET Core generates the document and stops there. Add `Scalar.AspNetCore` (what Microsoft's samples use now) or keep `Swashbuckle.AspNetCore.SwaggerUi` if your muscle memory insists on `/swagger`. Either way, gate it behind `IsDevelopment()` — a public schema endpoint is a free reconnaissance map for anyone probing your service.
- **`.WithOpenApi()` is deprecated** in .NET 10 (`ASPDEPR002`) and will be removed. Plenty of samples still show it. Use `.WithSummary()`, `.WithDescription()`, `.WithTags()`, or `.AddOpenApiOperationTransformer(...)` instead.
- **YAML is one argument**: `app.MapOpenApi("/openapi/{documentName}.yaml")`. Runtime only — build-time document generation is still JSON.
- **3.1 changes the schema output.** There's no `nullable: true` anymore; nullability shows up as a union type. If a downstream code generator chokes, drop back with `options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0` inside `AddOpenApi()`.

Want a description on a response the return type can't express? Attributes carry one now:

```csharp
[ProducesResponseType<OrderV2Response>(StatusCodes.Status200OK,
    Description = "The order, including its placement timestamp.")]
```


![Image description](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/tojb0v5tacpnf2i4ewnb.jpg)

## The whole thing

`Program.cs`:

```csharp
using Asp.Versioning;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<OrdersStore>();
builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddProblemDetails();
builder.Services.AddValidation();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = ApiVersionReader.Combine(
        new UrlSegmentApiVersionReader(),
        new HeaderApiVersionReader("X-Api-Version"));
})
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
})
.AddOpenApi();

var app = builder.Build();

var orders = app.NewVersionedApi("Orders");

var v1 = orders.MapGroup("/api/v{version:apiVersion}/orders")
               .HasApiVersion(1.0)
               .WithTags("Orders");

var v2 = orders.MapGroup("/api/v{version:apiVersion}/orders")
               .HasApiVersion(2.0)
               .WithTags("Orders");

v1.MapGet("/{id}", OrderEndpoints.GetOrderV1);
v1.MapPost("/", OrderEndpoints.PlaceOrder).AddEndpointFilter<StockReservationFilter>();

v2.MapGet("/{id}", OrderEndpoints.GetOrderV2);
v2.MapGet("/", OrderEndpoints.ListOrders);
v2.MapPost("/", OrderEndpoints.PlaceOrder).AddEndpointFilter<StockReservationFilter>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference();
}

app.Run();
```

The contracts and handlers:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

// Domain model — never leaves the process.
public sealed record Order(string Id, string Sku, int Quantity, string Status, DateTimeOffset PlacedAt);

// Wire contracts — one per version, free to diverge.
public sealed record OrderV1Response(string Id, string Sku, int Quantity, string Status);
public sealed record OrderV2Response(string Id, string Sku, int Quantity, string State, DateTimeOffset PlacedAt);

public sealed record PlaceOrderRequest(
    [property: Required]
    [property: RegularExpression(
        @"^[A-Z]{3}-[A-Z0-9]{2,4}$",
        ErrorMessage = "SKU must look like ETH-250 or COL-1KG.")]
    string Sku,

    [property: Range(1, 100, ErrorMessage = "Orders are capped at 100 units per line.")]
    int Quantity);

internal static class OrderEndpoints
{
    /// <summary>Looks up a single order by its identifier.</summary>
    /// <param name="id">The order identifier, for example <c>A-2001</c>.</param>
    public static Results<Ok<OrderV1Response>, NotFound> GetOrderV1(string id, OrdersStore store)
        => store.Find(id) is { } order
            ? TypedResults.Ok(new OrderV1Response(order.Id, order.Sku, order.Quantity, order.Status))
            : TypedResults.NotFound();

    /// <summary>Looks up a single order, including its placement timestamp.</summary>
    /// <param name="id">The order identifier, for example <c>A-2001</c>.</param>
    public static Results<Ok<OrderV2Response>, NotFound> GetOrderV2(string id, OrdersStore store)
        => store.Find(id) is { } order
            ? TypedResults.Ok(new OrderV2Response(
                order.Id, order.Sku, order.Quantity, order.Status, order.PlacedAt))
            : TypedResults.NotFound();

    /// <summary>Lists every order currently on file.</summary>
    public static Ok<IReadOnlyCollection<OrderV2Response>> ListOrders(OrdersStore store)
        => TypedResults.Ok<IReadOnlyCollection<OrderV2Response>>(
            store.All()
                 .Select(o => new OrderV2Response(o.Id, o.Sku, o.Quantity, o.Status, o.PlacedAt))
                 .ToArray());

    /// <summary>Places a new order and returns its location.</summary>
    public static Created PlaceOrder(PlaceOrderRequest request, OrdersStore store, HttpContext http)
    {
        var order = store.Place(request.Sku, request.Quantity);
        return TypedResults.Created($"{http.Request.Path}/{order.Id}");
    }
}
```

And the store, thread-safe because a singleton serving concurrent requests has no other option:

```csharp
using System.Collections.Concurrent;

public sealed class OrdersStore
{
    private readonly ConcurrentDictionary<string, Order> _orders = new()
    {
        ["A-2001"] = new("A-2001", "ETH-250", 2, "processing", DateTimeOffset.UtcNow),
        ["A-2002"] = new("A-2002", "COL-1KG", 1, "shipped", DateTimeOffset.UtcNow),
    };

    // Starts past the seeded IDs so the first generated order is A-2003.
    private int _nextOrderId = 2002;

    public Order? Find(string id) => _orders.TryGetValue(id, out var order) ? order : null;

    public IReadOnlyCollection<Order> All() => _orders.Values.ToArray();

    public Order Place(string sku, int quantity)
    {
        var id = Interlocked.Increment(ref _nextOrderId);
        var order = new Order($"A-{id}", sku, quantity, "processing", DateTimeOffset.UtcNow);
        _orders[order.Id] = order;
        return order;
    }
}
```

`ConcurrentDictionary` rather than `Dictionary` isn't decoration. A singleton store mutated from concurrent requests through a plain `Dictionary` can corrupt its internal buckets during a resize — the classic symptom is a request that spins a CPU core forever instead of throwing something you could debug at 3 a.m.

## When Minimal APIs — and when controllers

Reach for Minimal APIs when the surface is a set of endpoints rather than a resource hierarchy, when startup time and footprint matter (they're the only option under Native AOT), and — increasingly — when you want the newest framework features first. That last one is not a small point: **the built-in validation in this post does not support MVC or Razor Pages.** Minimal APIs and Blazor only. The investment is visibly flowing one way.

Stay with controllers when you have a large conventional CRUD surface where `[ApiController]`'s conventions genuinely save code, when you depend on the MVC filter ecosystem or model binders you'd have to rebuild by hand, or when you're extending an existing MVC app and consistency beats novelty. "We already have forty controllers" is a legitimate engineering reason, not a confession.

**Rule of thumb: new services start Minimal; existing MVC apps stay MVC until something concrete forces the move.** And if someone tells you Minimal APIs "don't scale to real projects," that usually means they wrote all forty endpoints as lambdas in one `Program.cs`. That's not the framework's fault — that's the same person who'd have written a two-thousand-line controller.

## Key Takeaways

- **`AddValidation()` makes DataAnnotations work on Minimal APIs** — no FluentValidation, no hand-rolled filter, no package reference in a web project. On positional records, `[property:]` is mandatory or validation silently does nothing.
- **Filters own the rules that need services** — DataAnnotations validate shape, `IEndpointFilter` validates reality. Keep the check-then-act race out of it by making the operation itself atomic.
- **Version the endpoints that broke, not the API** — a response-shape change is versioned; a `201 Created` with just a `Location` header often doesn't need to be.
- **`TypedResults` and `Results<T1, T2>` replace `.Produces<T>()`** — the signature becomes the OpenAPI contract, and the compiler enforces it.
- **.NET 10's OpenAPI defaults changed** — 3.1 documents, YAML at runtime, no bundled UI, and `.WithOpenApi()` deprecated. Half the samples online are still on the .NET 8 shape.
