using System.Text.Json;

namespace WinnersPortal.Services.Ai;

/// <summary>
/// The embedding request and response of each provider that offers one,
/// kept pure like <see cref="AiProviderRequests"/>. An embedding is a
/// list of numbers that stands for a text's meaning; two texts that mean
/// alike have vectors that point alike, and <see cref="Cosine"/> is how
/// alike. Nothing on the portal reads one yet: the eval tool's matching
/// run (<c>--matching</c>) is where they are measured against today's
/// Recommended readings first, and the feed keeps the model's reading of
/// a profile until that run says the vectors agree with it.
/// </summary>
public static class AiEmbeddings
{
    /// <summary>Gemini's name for "these vectors will be compared with each other", which shapes them for it.</summary>
    public const string TaskType = "SEMANTIC_SIMILARITY";

    /// <summary>Texts per request: Gemini's batch takes a hundred, OpenAI's more; one request is one activity row.</summary>
    public const int MaxTexts = 100;

    /// <summary>The embedding model a blank model name resolves to; Anthropic offers none.</summary>
    public static string DefaultModel(string provider) => provider switch
    {
        AiProviders.Gemini => "gemini-embedding-2",
        AiProviders.OpenAi => "text-embedding-3-small",
        AiProviders.Anthropic => throw new AiProviderException(0,
            "Anthropic offers no embedding model — embeddings run on Gemini or OpenAI."),
        _ => throw new AiProviderException(0, $"Unknown AI provider '{provider}' — the portal supports {AiProviders.Named}."),
    };

    public static AiProviderRequests.AiRequest Build(string provider, string model, string apiKey, IReadOnlyList<string> texts)
    {
        if (texts.Count is 0 or > MaxTexts)
            throw new ArgumentOutOfRangeException(nameof(texts), texts.Count, $"An embedding request carries 1 to {MaxTexts} texts.");
        return provider switch
        {
            AiProviders.Gemini => new AiProviderRequests.AiRequest(
                $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:batchEmbedContents",
                new Dictionary<string, string> { ["x-goog-api-key"] = apiKey },
                JsonSerializer.Serialize(new
                {
                    requests = texts.Select(text => new
                    {
                        model = $"models/{model}",
                        content = new { parts = new[] { new { text } } },
                        taskType = TaskType,
                    }),
                })),
            AiProviders.OpenAi => new AiProviderRequests.AiRequest(
                "https://api.openai.com/v1/embeddings",
                new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
                JsonSerializer.Serialize(new { model, input = texts })),
            AiProviders.Anthropic => throw new AiProviderException(0,
                "Anthropic offers no embedding model — embeddings run on Gemini or OpenAI."),
            _ => throw new AiProviderException(0, $"Unknown AI provider '{provider}' — the portal supports {AiProviders.Named}."),
        };
    }

    /// <summary>
    /// One vector per text sent, in the order sent — OpenAI's carry an
    /// index and are put in that order; Gemini's come in order. A body with
    /// a different count, or a vector that is not numbers, is thrown as
    /// the provider's failure rather than read as far as it goes.
    /// </summary>
    public static IReadOnlyList<float[]> ReadVectors(string provider, string responseJson, int expected)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var vectors = provider switch
        {
            AiProviders.Gemini => root.TryGetProperty("embeddings", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => Vector(e, "values")).ToList()
                : [],
            AiProviders.OpenAi => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Select(e => (Index: e.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : -1, Vector: Vector(e, "embedding")))
                    .OrderBy(x => x.Index)
                    .Select(x => x.Vector)
                    .ToList()
                : [],
            _ => throw new AiProviderException(0, $"Unknown AI provider '{provider}' — the portal supports {AiProviders.Named}."),
        };
        if (vectors.Count != expected || vectors.Any(v => v.Length == 0))
            throw new AiProviderException(0,
                $"The provider answered with {vectors.Count} embedding(s) for {expected} text(s).", AiFailure.Empty);
        return vectors;
    }

    private static float[] Vector(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetSingle() : float.NaN).ToArray()
            : [];

    /// <summary>
    /// How alike two vectors point, from 1 (the same direction) through 0
    /// (unrelated) to -1 (opposite). Zero for a vector of all zeros or one
    /// of another length, which is no comparison at all.
    /// </summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>What an embedding call answered: one vector per text sent, and the tokens the provider counted — null where it counted none.</summary>
public sealed record AiEmbedding(IReadOnlyList<float[]> Vectors, AiTokens? Tokens);
