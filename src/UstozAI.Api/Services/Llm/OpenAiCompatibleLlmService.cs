using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UstozAI.Api.Services.Mentor;

namespace UstozAI.Api.Services.Llm;

/// <summary>One OpenAI-compatible chat endpoint (Google Gemini, Groq, …). Enabled when it has an API key.</summary>
public class OpenAiCompatOptions
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 3000;
    /// <summary>For reasoning models (e.g. gpt-oss): "low" keeps the first word fast. Null = don't send.</summary>
    public string? ReasoningEffort { get; set; }
    /// <summary>Can the model look at images? If not, attached pictures are described as "(rasm)".</summary>
    public bool Vision { get; set; }
    /// <summary>Free tiers may use conversations for training — such a provider goes last for students under 16.</summary>
    public bool TrainsOnFreeTier { get; set; }
    /// <summary>response_format json_object. Groq can't stream in JSON mode → false there (the prompt enforces JSON).</summary>
    public bool JsonMode { get; set; } = true;
    /// <summary>How many recent messages to send (fewer = faster + fits the free per-minute token limits).</summary>
    public int HistoryTurns { get; set; } = 8;
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>An LLM call failed with an HTTP status (rate limit, quota, auth …) — the chain decides what to do.</summary>
public class LlmProviderException(string provider, HttpStatusCode? status, string detail)
    : Exception($"{provider}: {(int?)status} {detail}")
{
    public string Provider { get; } = provider;
    public HttpStatusCode? Status { get; } = status;
    public string Detail { get; } = detail;
}

/// <summary>
/// Teacher on any OpenAI-compatible endpoint. Same persona, brief and JSON contract as Claude; the reply streams
/// token by token (Server-Sent Events) so the first sentence can be spoken before the answer is complete.
/// The HttpClient comes from IHttpClientFactory on every call (fresh DNS, pooled sockets).
/// </summary>
public class OpenAiCompatibleLlmService(OpenAiCompatOptions o, IHttpClientFactory http) : ILlmService
{
    public string Name => o.Name;
    public bool TrainsOnFreeTier => o.TrainsOnFreeTier;

    /// <summary>
    /// A compact contract instead of the full JSON schema (~1 600 tokens saved): the exact shape by example, in the
    /// order the streaming parser needs (header first, then speechSegments — so the first sentence is spoken early).
    /// </summary>
    private const string FormatNote = """

        # CHIQISH (QAT'IY)
        Javob — FAQAT bitta JSON obyekt (markdown yo'q, ``` yo'q). Kalitlar AYNAN shu tartibda, namunadagidek:
        {"emotion":"teaching","rageLevel":0,"shyLevel":0,"userMood":"neutral","replyLanguage":"uz-UZ",
         "eyeAndFaceCues":[{"cue":"head_nod","segment":0,"at":0.3}],"pivotToTopic":"Present Perfect",
         "bodyAction":{"action":"none","beforeSegment":-1},
         "speechSegments":[{"type":"speech","emotion":"teaching","text":"1–2 qisqa gap","intensity":1}],
         "safety":{"category":"none","note":""},
         "boardContent":{"show":false,"type":"grammar","title":"","markdownBody":"","highlightWords":[]},
         "mistakes":[]}
        emotion: neutral|laughing|mocking|shy_flustered|angry_flame|hurt_apologetic|thinking|teaching|annoyed_cutoff.
        userMood: neutral|amused|hurt|frustrated|stressed. speechSegments type: speech|laugh_sfx|sigh_sfx|gasp_sfx|scoff_sfx|pause.
        cue: head_nod, head_shake, brow_furrow, eye_roll, smirk, wide_eyes, bashful_look, slow_blink, mouth_smile, curious_inspect.
        mistakes: aniq xato bo'lsa [{"category":"grammar","userSaid":"…","correctForm":"…","explanation":"…"}], aks holda [].
        boardContent faqat yangi qoida/jadval bo'lsa show=true.
        """;

    public async IAsyncEnumerable<string> StreamReplyAsync(IReadOnlyList<ChatTurn> history, LlmContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = MentorPersona.Compact(context.Language, context.Style) + "\n" + context.MentorBrief +
                              MentorPersona.Continuity(context.SessionDate, context.Today) + FormatNote,
            },
        };
        foreach (var t in ClaudeLlmService.NormalizeHistory(history, o.HistoryTurns))
            messages.Add(t.IsUser ? UserMessage(t) : new JsonObject { ["role"] = "assistant", ["content"] = t.Text });

        var body = new JsonObject
        {
            ["model"] = o.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["max_tokens"] = o.MaxTokens,
        };
        // Groq can't stream in JSON mode (the whole reply would arrive at once) → off there; the prompt enforces JSON.
        if (o.JsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (!string.IsNullOrWhiteSpace(o.ReasoningEffort)) body["reasoning_effort"] = o.ReasoningEffort;

        using var req = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", o.ApiKey);
        var client = http.CreateClient("llm");
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = await res.Content.ReadAsStringAsync(ct);
            throw new LlmProviderException(o.Name, res.StatusCode, detail.Length > 400 ? detail[..400] : detail);
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await foreach (var piece in ReadSse(reader, ct)) yield return piece;
    }

    /// <summary>Server-Sent Events → the text deltas (choices[0].delta.content). Ends at "data: [DONE]".</summary>
    public static async IAsyncEnumerable<string> ReadSse(TextReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            if (data.Length == 0) continue;
            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new LlmProviderException("stream", null, err.ToString());
                if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                    && ch[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    text = c.GetString();
            }
            catch (JsonException) { /* keep-alive / partial line — skip */ }
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    private JsonObject UserMessage(ChatTurn t)
    {
        var text = string.IsNullOrWhiteSpace(t.Text) ? "(rasm)" : t.Text;
        if (t.Images is not { Count: > 0 }) return new JsonObject { ["role"] = "user", ["content"] = text };
        if (!o.Vision)
            return new JsonObject { ["role"] = "user", ["content"] = $"{text}\n(O'quvchi {t.Images.Count} ta rasm yubordi, lekin hozir rasmni ko'ra olmaysan — buni ochiq ayt.)" };
        var parts = new JsonArray();
        foreach (var img in t.Images)
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Base64}" },
            });
        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        return new JsonObject { ["role"] = "user", ["content"] = parts };
    }
}
