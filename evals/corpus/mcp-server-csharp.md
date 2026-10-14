# Build an MCP Server in C#: Write Your Tools Once, Use Them in Every Claude

# Build an MCP Server in C#: Write Your Tools Once, Use Them in Every Claude

Last time we [built a Claude tool-use agent in C#](https://dev.to/jgomezdev/build-a-claude-tool-use-agent-in-c-not-a-chatbot-on-steroids-3e9g). It worked — but every tool lived *inside that one console app*. If you wanted the same "look up an order" ability inside Claude Desktop, or Claude Code, or a teammate's agent, you'd copy-paste the tool and its plumbing into each one. Tools trapped in a single process, re-implemented everywhere. That's the coupling MCP exists to break.

The **Model Context Protocol** flips the ownership around. You write a tool *once*, as a standalone server, and expose it over a standard protocol. Then any MCP client — Claude Desktop, Claude Code, your own agent, whatever ships next quarter — connects and gets your tools for free. In this post we'll take the exact same Aurora Coffee Co. tools from last time and set them loose as a real MCP server in .NET, then plug it into Claude.



## What MCP actually is

MCP is a small client/server protocol (JSON-RPC under the hood) for connecting AI clients to your capabilities. Three ideas are all you need to start:

1. **Client and server are separate processes.** The *client* is the AI app (Claude Desktop, Claude Code). The *server* is your code. They talk over a transport, not a shared function table.
2. **Servers expose three things:** **tools** (actions the model can call — our focus), **resources** (readable data, like files or rows), and **prompts** (reusable prompt templates). Today it's all tools.
3. **Two transports.** **stdio** — the client launches your server as a child process and talks over standard in/out; perfect for local tools. **HTTP** — your server runs somewhere and clients connect over the network; for shared or remote tools.

If you read the previous article, this is the same tool-use loop — declare a tool, the model calls it, your code runs it, you return a result — but the tool now lives on the *other side of a process boundary*, reusable by any client instead of hardwired into one app.



---

## The server we're building

Remember **Aurora Coffee Co.**? Its two tools answered support questions over some in-memory data:

- `get_order_status(order_id)` — looks up an order.
- `check_stock(sku)` — checks inventory for a product.

Last time those tools were methods buried in an agent loop. Now they become an MCP server that *any* Claude can call. Start a console app and add two packages:

```bash
dotnet new console -o AuroraCoffee.Mcp
cd AuroraCoffee.Mcp
dotnet add package ModelContextProtocol
dotnet add package Microsoft.Extensions.Hosting
```

`ModelContextProtocol` is the official C# SDK (2.0, maintained with Microsoft). `Microsoft.Extensions.Hosting` gives us the generic host — the same DI and configuration story you'd use in any .NET service.

### The data, as an injectable service

We'll hold Aurora's "database" in a singleton so tools can take it via constructor injection — exactly like a real repository:

```csharp
public sealed class CoffeeShopData
{
    public Dictionary<string, Order> Orders { get; } = new()
    {
        ["A-1001"] = new("A-1001", "shipped", "2026-08-03"),
        ["A-1002"] = new("A-1002", "processing", "2026-08-06"),
    };

    public Dictionary<string, int> Stock { get; } = new()
    {
        ["ETH-250"] = 42,
        ["COL-1KG"] = 0,
    };
}

public sealed record Order(string Id, string Status, string Eta);
```

### The tools

Here's the part that replaces last time's hand-written JSON Schema and `switch` statement. A class marked `[McpServerToolType]`, methods marked `[McpServerTool]`, and `[Description]` on both the method and its parameters. The SDK reads your **method signature** and generates the JSON Schema for you:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class CoffeeTools(CoffeeShopData data)
{
    [McpServerTool, Description("Look up the status and ETA of a customer order by its id.")]
    public string GetOrderStatus(
        [Description("Order id, e.g. A-1001")] string orderId) =>
        data.Orders.TryGetValue(orderId, out var order)
            ? $"Order {order.Id}: {order.Status}, ETA {order.Eta}."
            : "No order found with that id.";

    [McpServerTool, Description("Check how many units of a product SKU are currently in stock.")]
    public string CheckStock(
        [Description("Product SKU, e.g. ETH-250")] string sku) =>
        data.Stock.TryGetValue(sku, out var quantity)
            ? $"SKU {sku}: {quantity} units in stock."
            : "Unknown SKU.";
}
```

Those `[Description]` attributes are not decoration — they're the same load-bearing docstrings from the last article, just written in a nicer place. The method one tells Claude *when* to call the tool; the parameter ones tell it *what to pass*. Vague descriptions here are the number-one reason a model ignores a perfectly good tool.

Notice what's gone: no `input_schema` object, no `required` array, no manual argument parsing. `orderId` is a typed `string` parameter, so the SDK knows it's a required string and hands you the parsed value. The plumbing from Approach 1 last time is now the framework's job.

### Wiring it together

The host builder registers the data, the MCP server, the stdio transport, and auto-discovers every `[McpServerToolType]` in the assembly:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout is the protocol channel — logs MUST go to stderr or you corrupt the stream.
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<CoffeeShopData>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

That's the whole server. Read that logging line again, because it's the single biggest footgun in stdio MCP: **stdout carries the JSON-RPC messages**. One stray `Console.WriteLine` to stdout and Claude sees line noise where it expected JSON — the protocol equivalent of talking with your mouth full. Route every log to stderr and you're fine.

---

## Plugging it into Claude

A server nobody connects to is just a very lonely console app. Let's wire it into two clients.

**Claude Desktop** reads a JSON config file (`claude_desktop_config.json`, in `%APPDATA%\Claude\` on Windows or `~/Library/Application Support/Claude/` on macOS). Add your server under `mcpServers`:

```json
{
  "mcpServers": {
    "aurora-coffee": {
      "command": "dotnet",
      "args": ["run", "--project", "C:/src/AuroraCoffee.Mcp"]
    }
  }
}
```

The client runs that command, speaks stdio to the child process, and your tools show up. Restart Claude Desktop and ask *"Is order A-1001 shipped, and do you still have ETH-250 in stock?"* — it'll call both tools and answer for real.

**Claude Code** is even shorter — one CLI command, no file editing:

```bash
claude mcp add aurora-coffee -- dotnet run --project ./AuroraCoffee.Mcp
```

Same server, second client, zero code changes. *That's the entire point of MCP*: the tools didn't move, but now two different Claudes can reach them — and so can the third one you haven't installed yet.

### Test it without a client

Before you touch any client config, sanity-check the server with the **MCP Inspector**, a browser UI that speaks the protocol so you can list and invoke tools by hand:

```bash
npx @modelcontextprotocol/inspector dotnet run --project ./AuroraCoffee.Mcp
```

It launches your server, lists `get_order_status` and `check_stock`, and lets you fire test calls. If a tool misbehaves, you find out here — not three layers deep inside a chat transcript.



---

## Reaching the outside world: DI and HttpClient

In-memory dictionaries are fine for a demo, but real tools call real systems. Because the server is a normal .NET host, dependency injection works exactly as you'd expect — including typed `HttpClient`s. Suppose Aurora has a roastery API for tasting notes:

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public sealed class RoastTools(HttpClient http)
{
    [McpServerTool, Description("Fetch today's roast notes for a coffee origin.")]
    public async Task<string> GetRoastNotes(
        [Description("Origin name, e.g. Ethiopia")] string origin) =>
        await http.GetStringAsync(
            $"https://api.auroracoffee.example/roast-notes/{Uri.EscapeDataString(origin)}");
}
```

Register it as a typed client and you're done — the SDK injects the `HttpClient` into the tool's constructor:

```csharp
builder.Services.AddHttpClient<RoastTools>();
```

Two things worth calling out. First, tools can be `async` and return `Task<string>` — no ceremony, just `await`. Second, use `AddHttpClient<T>` (or `IHttpClientFactory`) rather than newing up an `HttpClient` per call; the factory pools connections so you don't exhaust sockets under load. Standard .NET hygiene applies — MCP doesn't change any of it.

---

## Going remote: the HTTP transport

stdio is great when the client can launch your server locally. But if you want *one* server shared across a team, or running in a container, you want HTTP. Swap the transport package and two lines:

```bash
dotnet add package ModelContextProtocol.AspNetCore
```

```csharp
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CoffeeShopData>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();
app.Run();
```

Same `CoffeeTools`, same attributes, same discovery — only the transport and host changed. Now clients connect by URL instead of launching a process, and you get everything ASP.NET Core already gives you: auth middleware, HTTPS, hosting, scaling. Your tools didn't even notice.

One note if you're on SDK 2.0 (shipped July 28, 2026, implementing the 2026-07-28 spec revision): **the HTTP transport is stateless by default** — `HttpServerTransportOptions.Stateless` defaults to `true`, the `initialize` handshake is gone, and the `Mcp-Session-Id` header went away. For plain request/response tools like Aurora's, nothing changes and you get easier scaling behind a load balancer. If your server leans on sessions or server-initiated requests (sampling, roots — now deprecated), read the migration notes before upgrading.

---

## When to build an MCP server — and when not to

Reach for MCP when tools need to be **shared or reused**: the same capability across Claude Desktop *and* Claude Code, a server your whole team points at, or tools you want decoupled from any single app and free to evolve on their own release cadence. The process boundary is a feature — it's what lets one server serve many clients, in any language.

Skip it when exactly one app will ever use the tools and it's the app doing the model calls. Then the in-app tool-use loop from the previous article is simpler and faster — no transport, no second process. A server whose only client is itself is just a function call wearing a lanyard.

**Rule of thumb: in-app tool use when one app owns the model and the tools; an MCP server the moment more than one client needs the same tools.**

---

## Key Takeaways

- **MCP decouples tools from clients** — write a tool once as a server and every MCP client (Claude Desktop, Claude Code, your own agent) can use it, instead of re-implementing it in each app.
- **The SDK is attribute-driven** — `[McpServerToolType]` on the class, `[McpServerTool]` on the method, and the JSON Schema is generated from your signature. No hand-written schema, no manual argument parsing.
- **Descriptions are still load-bearing** — `[Description]` on the method and each parameter is how Claude decides when to call a tool and what to pass. Write them like docstrings.
- **Guard stdout** — in stdio mode the protocol *is* stdout. Send logs to stderr or you'll corrupt the stream with your own debug output.
- **Same tools, swap the transport** — stdio for local child-process tools, HTTP (`ModelContextProtocol.AspNetCore`) for shared or remote ones. The tool code doesn't change.
