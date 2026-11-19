using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace UstozAI.Api.Services.Media;

/// <summary>What the mentor asked for: edit the user's image, or generate a new one.</summary>
public record ImageJob(string Action, string Prompt, int Count, byte[]? Source, string? SourceType);

/// <summary>Result: real images (bytes), or — offline — a list of client-side filter presets to render.</summary>
public record ImageJobResult(IReadOnlyList<byte[]> Images, IReadOnlyList<string>? ClientPresets = null);

/// <summary>Image editing/generation provider (OpenAI gpt-image-1 today; swap for Imagen/Gemini/Stability).</summary>
public interface IImageGenerator
{
    string Name { get; }
    Task<ImageJobResult> RunAsync(ImageJob job, CancellationToken ct);
}

public class OpenAiImageOptions
{
    public string? ApiKey { get; set; }                // env: OpenAIImages__ApiKey
    public string Model { get; set; } = "gpt-image-1";
    public string Size { get; set; } = "1024x1024";
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// OpenAI Images API: POST /v1/images/edits (multipart, with the source image) or /v1/images/generations (JSON).
/// gpt-image-1 returns base64 (b64_json). Provider-side moderation applies on top of our own safety gate.
/// </summary>
public class OpenAiImageGenerator(HttpClient http, IOptions<OpenAiImageOptions> options) : IImageGenerator
{
    public string Name => "openai";

    public async Task<ImageJobResult> RunAsync(ImageJob job, CancellationToken ct)
    {
        var o = options.Value;
        using var req = new HttpRequestMessage(HttpMethod.Post,
            job.Action == "edit" && job.Source is not null
                ? "https://api.openai.com/v1/images/edits"
                : "https://api.openai.com/v1/images/generations");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);

        if (job.Action == "edit" && job.Source is not null)
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(o.Model), "model" },
                { new StringContent(job.Prompt), "prompt" },
                { new StringContent(job.Count.ToString()), "n" },
                { new StringContent(o.Size), "size" },
            };
            var img = new ByteArrayContent(job.Source);
            img.Headers.ContentType = MediaTypeHeaderValue.Parse(job.SourceType ?? "image/png");
            form.Add(img, "image", "source" + (job.SourceType == "image/jpeg" ? ".jpg" : ".png"));
            req.Content = form;
        }
        else
        {
            req.Content = JsonContent.Create(new { model = o.Model, prompt = job.Prompt, n = job.Count, size = o.Size });
        }

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Image API {(int)res.StatusCode}: {body[..Math.Min(body.Length, 300)]}");

        using var doc = JsonDocument.Parse(body);
        var images = doc.RootElement.GetProperty("data").EnumerateArray()
            .Where(d => d.TryGetProperty("b64_json", out _))
            .Select(d => Convert.FromBase64String(d.GetProperty("b64_json").GetString()!))
            .ToList();
        return new ImageJobResult(images);
    }
}

/// <summary>
/// No image API key: the browser renders stylized variations of the source image on a canvas
/// (sketch / pop-art / sepia / pixel / mirror / noir). Keeps the feature demoable offline.
/// </summary>
public class ClientFilterImageGenerator : IImageGenerator
{
    public static readonly string[] Presets = ["sketch", "popart", "sepia", "pixel", "mirror", "noir"];
    public string Name => "browser-filters";

    public Task<ImageJobResult> RunAsync(ImageJob job, CancellationToken ct)
    {
        var p = job.Prompt.ToLowerInvariant();
        // Pick presets that match the request words first, then fill up.
        var ranked = Presets.OrderByDescending(x => p.Contains(x) || (x == "sketch" && p.Contains("chiz"))
                                                  || (x == "noir" && (p.Contains("oq-qora") || p.Contains("black")))
                                                  || (x == "popart" && p.Contains("rang")))
                            .Take(Math.Clamp(job.Count, 1, 4)).ToList();
        return Task.FromResult(new ImageJobResult([], ranked));
    }
}
