# Build a Local RAG in .NET: Chat With Your Docs (No Cloud, No API Keys)

# Build a Local RAG in .NET: Chat With Your Docs (No Cloud, No API Keys)

Ask a language model about *your* product's documentation and watch it improvise. It has never read your internal API docs, so it fills the gaps with confident, plausible, completely made-up answers. That is hallucination, and it is not the model being dumb — it is the model being asked a question it was never given the material to answer.

RAG fixes this. Instead of hoping the model *memorized* your docs, you hand it the relevant pages *at question time* — like letting a student take an open-book exam. In this tutorial we build a small RAG system in .NET 10 that answers questions about a product's documentation, and we do it **100% locally**: no cloud, no API keys, no invoice at the end of the month.


![Image description](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/w5f6dlr2gt58fo1u2iof.jpg)

---

## What Is RAG, Without the Hype?

RAG stands for **Retrieval-Augmented Generation**. Strip away the acronym and it is three plain-English words:

- **Retrieval** — find the pieces of your docs that relate to the question.
- **Augmented** — glue those pieces onto the prompt.
- **Generation** — let the model write the answer using them.

That's it. The clever part is step one: *how do you find the relevant pieces?* You can't just search for matching keywords, because someone might ask "how do I log in?" while your docs say "authentication." Different words, same meaning.

The trick is **embeddings**. An embedding is just a list of numbers (a *vector*) that captures the *meaning* of a piece of text. Texts that mean similar things get similar numbers — so "log in" and "authentication" land close together, even though they share no letters. Find the closest vectors to the question's vector, and you've found the relevant docs. No magic, just geometry.

---

## The Three Steps of RAG

Here is the entire pipeline, start to finish:

1. **Ingest** (done once, up front): chop the docs into small chunks, turn each chunk into an embedding, and store them.
2. **Retrieve** (done per question): turn the question into an embedding, then find the chunks whose embeddings are closest to it.
3. **Augment & generate** (done per question): drop those chunks into the prompt and ask the model to answer using them.

Steps 2 and 3 run every time someone asks something. Step 1 runs once and gets reused forever (or until your docs change).


![Image description](https://dev-to-uploads.s3.us-east-2.amazonaws.com/uploads/articles/g3dowy7ne2l47db6imfm.png)

---

## What We're Building + Setup

We'll build a console app that ingests a product's docs and then answers questions about them from the terminal. Three tools do the heavy lifting:

- **Ollama** runs the AI models on your machine — one to create embeddings, one to write answers.
- **SQLite** stores everything in a single file.
- **sqlite-vec**, a SQLite extension, does the "find the closest vectors" search for us.

Your laptop is about to moonlight as a tiny AI data center. The only cost is a slightly warmer fan.

```bash
# 1. Create the project
dotnet new console -n OrbitDocsRag
cd OrbitDocsRag
dotnet add package Microsoft.Data.Sqlite

# 2. Install Ollama from https://ollama.com, then pull the two models
ollama pull nomic-embed-text   # turns text into 768-number vectors
ollama pull llama3.2           # writes the final answer

# 3. Drop the sqlite-vec native library next to your app.
#    Grab vec0.dll (Windows) / vec0.so (Linux) / vec0.dylib (macOS)
#    from https://github.com/asg017/sqlite-vec/releases and place it
#    in the project folder so it lands in bin/ at build time.
```

Ollama exposes a plain REST API at `http://localhost:11434`. We'll talk to it with a bare `HttpClient` — no SDK — so you can see there is nothing hiding behind a wrapper. It really is just a couple of HTTP `POST`s.

---

## Step 1 — Chop the Docs Into Chunks

You can't embed a whole 50-page manual as one vector — it would be a blurry average of everything, useful for nothing. So we split each document into small, overlapping windows. Small chunks give *precise* matches; the overlap makes sure an idea that straddles a boundary isn't cut in half.

```csharp
sealed record DocChunk(string Source, string Text);

static class Chunker
{
    public static IEnumerable<DocChunk> Split(
        string source, string text, int maxWords = 120, int overlap = 20)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        for (var start = 0; start < words.Length; start += maxWords - overlap)
        {
            var end = Math.Min(start + maxWords, words.Length);
            yield return new DocChunk(source, string.Join(' ', words[start..end]));
            if (end == words.Length) break;
        }
    }
}
```

Each chunk remembers its `Source` (the file it came from) so we can cite it later. Slide a 120-word window forward 100 words at a time, and you get overlapping chunks that keep their context.

---

## Step 2 — Turn Text Into Vectors (Embeddings)

This is where Ollama earns its keep. We `POST` a string to `/api/embed` and get back an array of 768 floats — the embedding.

```csharp
using System.Net.Http.Json;
using System.Text.Json;

sealed class OllamaClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:11434") };

    public async Task<float[]> EmbedAsync(string text)
    {
        var request = new { model = "nomic-embed-text", input = text };
        var response = await _http.PostAsJsonAsync("/api/embed", request, Json);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(Json);
        return body!.Embeddings[0];
    }

    private sealed record EmbedResponse(float[][] Embeddings);
}
```

That's the whole "AI" part of embeddings — one HTTP call. The model reads your text and returns its meaning as 768 numbers. Two chunks about authentication will come back with two very similar arrays.

---

## Step 3 — Store the Vectors in SQLite

Now we save each chunk *and* its embedding. `sqlite-vec` gives us a special virtual table that can search vectors natively. We tell it the vectors are 768 floats and that we want **cosine** distance (more on that in a second). The `+source` and `+chunk` columns ride along as plain text so we can read the original text back after a search.

```csharp
using Microsoft.Data.Sqlite;
using System.Text.Json;

sealed class VectorStore(string path) : IDisposable
{
    private readonly SqliteConnection _connection = new($"Data Source={path}");

    public void Initialize()
    {
        _connection.Open();
        _connection.LoadExtension("vec0"); // loads the sqlite-vec native library

        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                embedding float[768] distance_metric=cosine,
                +source text,
                +chunk text
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Add(DocChunk chunk, float[] embedding)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO vec_chunks(embedding, source, chunk)
            VALUES ($embedding, $source, $chunk);
            """;
        command.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(embedding));
        command.Parameters.AddWithValue("$source", chunk.Source);
        command.Parameters.AddWithValue("$chunk", chunk.Text);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
```

We store the embedding as a JSON string like `[0.12, -0.04, ...]` — sqlite-vec happily parses that.

### How does "closest" actually work?

`sqlite-vec` does the search in fast native code, but there's no mystery inside. "How similar are two vectors?" is answered by **cosine similarity**, which is a handful of lines of plain C#:

```csharp
static double CosineSimilarity(float[] a, float[] b)
{
    double dot = 0, magA = 0, magB = 0;
    for (var i = 0; i < a.Length; i++)
    {
        dot  += a[i] * b[i];
        magA += a[i] * a[i];
        magB += b[i] * b[i];
    }
    return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
}
```

The result runs from `1.0` (identical meaning) down to `0` (unrelated). sqlite-vec reports the *distance* — basically `1 - similarity` — so **smaller is closer**. That's why every query below ends with `ORDER BY distance`. That's the whole "vector search" everyone talks about: a dot product and a square root.

---

## Step 4 — Retrieve the Relevant Chunks

Per question, we embed the question the exact same way, then ask sqlite-vec for the `k` nearest chunks. The `MATCH` keyword is sqlite-vec's signal to run a nearest-neighbor search.

```csharp
sealed record SearchHit(string Source, string Chunk, double Distance);

// (add this method to VectorStore)
public List<SearchHit> Search(float[] queryEmbedding, int k)
{
    using var command = _connection.CreateCommand();
    command.CommandText = """
        SELECT source, chunk, distance
        FROM vec_chunks
        WHERE embedding MATCH $query
        ORDER BY distance
        LIMIT $k;
        """;
    command.Parameters.AddWithValue("$query", JsonSerializer.Serialize(queryEmbedding));
    command.Parameters.AddWithValue("$k", k);

    var hits = new List<SearchHit>();
    using var reader = command.ExecuteReader();
    while (reader.Read())
        hits.Add(new SearchHit(reader.GetString(0), reader.GetString(1), reader.GetDouble(2)));

    return hits;
}
```

Give it the question's vector, get back the three chunks most likely to contain the answer — ranked from closest to furthest.

---

## Step 5 — Build the Prompt and Generate the Answer

The final step is the payoff. We stitch the retrieved chunks into a prompt with one strict instruction: **answer only from this context**. Then we send it to the chat model via `/api/chat`.

```csharp
// (add this method to OllamaClient)
public async Task<string> ChatAsync(string prompt)
{
    var request = new
    {
        model = "llama3.2",
        messages = new[] { new { role = "user", content = prompt } },
        stream = false
    };
    var response = await _http.PostAsJsonAsync("/api/chat", request, Json);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json);
    return body!.Message.Content;
}

// nested records inside OllamaClient
private sealed record ChatResponse(ChatMessage Message);
private sealed record ChatMessage(string Role, string Content);
```

And the prompt that ties it all together:

```csharp
var context = string.Join("\n\n", hits.Select(h => $"[{h.Source}] {h.Chunk}"));

var prompt = $"""
    Answer the question using ONLY the context below.
    If the answer is not in the context, say "I don't know based on the docs."

    Context:
    {context}

    Question: {question}
    """;
```

That one instruction — *only from this context* — is what kills hallucinations. With nothing to lean on, the model won't confidently invent an answer; it'll admit it doesn't know. It's the difference between a student who studied and one improvising in front of the class. And because each chunk carries its `[source.md]` tag, you can show the user exactly where the answer came from.

---

## The Full Example

Here's the top-level `Program.cs`. It ships with three tiny "docs" inline so you can run it without any files, but swap `SampleDocs.All` for a folder of real `.md` files and nothing else changes. Keep the `OllamaClient`, `VectorStore`, `Chunker`, `DocChunk`, and `SearchHit` types from the steps above in the same project (below the top-level code, or in their own files) and it compiles as-is.

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

var ollama = new OllamaClient();
using var store = new VectorStore("orbit-docs.db");
store.Initialize();

// --- Step 1 + 2 + 3: ingest the docs (chunk -> embed -> store) ---
foreach (var (source, text) in SampleDocs.All)
    foreach (var chunk in Chunker.Split(source, text))
    {
        var embedding = await ollama.EmbedAsync(chunk.Text);
        store.Add(chunk, embedding);
    }

Console.Write("Ask about the Orbit API: ");
var question = Console.ReadLine() is { Length: > 0 } input ? input : "How do I authenticate?";

// --- Step 4: retrieve ---
var questionEmbedding = await ollama.EmbedAsync(question);
var hits = store.Search(questionEmbedding, k: 3);

// --- Step 5: augment + generate ---
var context = string.Join("\n\n", hits.Select(h => $"[{h.Source}] {h.Chunk}"));
var prompt = $"""
    Answer the question using ONLY the context below.
    If the answer is not in the context, say "I don't know based on the docs."

    Context:
    {context}

    Question: {question}
    """;

var answer = await ollama.ChatAsync(prompt);

Console.WriteLine($"\n{answer}\n");
Console.WriteLine($"Sources: {string.Join(", ", hits.Select(h => h.Source).Distinct())}");

static class SampleDocs
{
    public static readonly Dictionary<string, string> All = new()
    {
        ["auth.md"] = """
            Authentication. The Orbit API uses API keys. Send your key in the
            Authorization header as 'Bearer <key>'. Keys never expire but can be
            revoked from the dashboard. Rate limits are 100 requests per minute.
            """,
        ["webhooks.md"] = """
            Webhooks. Orbit pushes events to your endpoint. Register a webhook URL
            in the dashboard. Every payload is signed with HMAC-SHA256; verify the
            X-Orbit-Signature header before trusting the body. Failed deliveries
            retry 5 times with exponential backoff.
            """,
        ["errors.md"] = """
            Error codes. Orbit returns standard HTTP status codes. A 429 means you
            hit the rate limit, so back off and retry. A 401 means your API key is
            missing or invalid. A 422 means the request body failed validation.
            """
    };
}
```

Run it, ask *"how do I log in?"*, and even though those exact words appear nowhere in the docs, the embedding for your question lands next to the authentication chunk — and the model answers with the API-key instructions, citing `auth.md`. That's RAG working.



---

## When to Use RAG — and When Not To

RAG is the right tool when your knowledge is **large, private, or changing**:

- **Use RAG** for company docs, product manuals, support tickets, a knowledge base — anything the base model never saw and that updates often. New docs? Just embed them. No retraining.
- **Skip RAG and just paste it into the prompt** when the whole knowledge fits comfortably in the context window. If it's two paragraphs, retrieval is overkill.
- **Reach for fine-tuning instead** when you need to change the model's *style, tone, or format* — not its facts. Fine-tuning teaches behavior; RAG supplies knowledge. They solve different problems, and plenty of real systems use both.

A few honest limitations to keep in mind: your answers are only as good as your chunks, so bad chunking means bad retrieval. Tables and numbers embed poorly (their meaning lives in structure, not prose). And if the answer simply isn't in the docs, a well-instructed RAG system should say so — which is a feature, not a bug.

The rule of thumb: **if you'd have to copy-paste a document into the chat to get a good answer, that's a job for RAG.**

---

## Key Takeaways

- **RAG is three steps, not magic** — retrieve relevant chunks, augment the prompt, generate the answer. Everything else is detail.
- **Embeddings turn meaning into geometry** — similar text becomes similar vectors, so you can find relevant docs even when the words don't match.
- **"Vector search" is just cosine similarity** — a dot product and a square root; sqlite-vec only makes it fast, not mysterious.
- **Grounding kills hallucinations** — telling the model to answer *only* from the retrieved context turns confident nonsense into honest "I don't know," and lets you cite sources.
- **You don't need the cloud** — Ollama plus SQLite runs a complete RAG pipeline on your laptop, with zero API keys and zero per-token cost.
