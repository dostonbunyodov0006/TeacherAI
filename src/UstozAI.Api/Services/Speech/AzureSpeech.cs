using System.Net.Http.Headers;
using System.Security;
using System.Text.Json;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Options;

namespace UstozAI.Api.Services.Speech;

public class AzureSpeechOptions
{
    public string? Key { get; set; }       // env: AzureSpeech__Key
    public string Region { get; set; } = "westeurope";
    public bool Enabled => !string.IsNullOrWhiteSpace(Key);
}

/// <summary>Azure "short audio" REST STT (≤60 s). Supports uz-UZ. Swap for streaming SDK recognition in phase 3b.</summary>
public class AzureSpeechToText(HttpClient http, IOptions<AzureSpeechOptions> options) : ISpeechToText
{
    public async Task<SttResult> TranscribeAsync(byte[] wav, string language, CancellationToken ct)
    {
        if (language == "auto") return await RecognizeAutoAsync(wav, ct);
        // English: also run unscripted pronunciation assessment (per-word accuracy). Falls back to plain STT on error.
        var assess = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        try { return await RecognizeAsync(wav, language, assess, ct); }
        catch (HttpRequestException) when (assess) { return await RecognizeAsync(wav, language, false, ct); }
    }

    /// <summary>
    /// Speech SDK with at-start language identification over the three conversation languages. The WAV body is
    /// pushed as 16 kHz PCM; continuous recognition collects every phrase of a long push-to-talk utterance.
    /// </summary>
    private async Task<SttResult> RecognizeAutoAsync(byte[] wav, CancellationToken ct)
    {
        var o = options.Value;
        var config = SpeechConfig.FromSubscription(o.Key, o.Region);
        var auto = AutoDetectSourceLanguageConfig.FromLanguages(LanguageSupport.All);
        using var push = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        push.Write(wav.AsSpan(44).ToArray());
        push.Close();
        using var audio = Microsoft.CognitiveServices.Speech.Audio.AudioConfig.FromStreamInput(push);
        using var rec = new SpeechRecognizer(config, auto, audio);

        var text = new System.Text.StringBuilder();
        string? lang = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rec.Recognized += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(e.Result.Text)) return;
            lock (text) text.Append(e.Result.Text).Append(' ');
            lang ??= AutoDetectSourceLanguageResult.FromResult(e.Result).Language;
        };
        rec.SessionStopped += (_, _) => done.TrySetResult();
        rec.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error) done.TrySetException(new InvalidOperationException($"Azure STT: {e.ErrorDetails}"));
            else done.TrySetResult();                                   // EndOfStream
        };
        await rec.StartContinuousRecognitionAsync();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(40), ct); }
        finally { await rec.StopContinuousRecognitionAsync(); }
        return new SttResult(text.ToString().Trim(), null, null, LanguageSupport.IsSupported(lang) ? lang : null);
    }

    private async Task<SttResult> RecognizeAsync(byte[] wav, string language, bool assess, CancellationToken ct)
    {
        var o = options.Value;
        var url = $"https://{o.Region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1" +
                  $"?language={Uri.EscapeDataString(language)}&format=detailed";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(wav) };
        req.Headers.Add("Ocp-Apim-Subscription-Key", o.Key);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav; codecs=audio/pcm; samplerate=16000");
        if (assess)
        {
            var cfg = JsonSerializer.Serialize(new
            {
                ReferenceText = "", GradingSystem = "HundredMark", Granularity = "Word", Dimension = "Comprehensive",
                EnableMiscue = false,
            });
            req.Headers.Add("Pronunciation-Assessment", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cfg)));
        }

        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.GetProperty("RecognitionStatus").GetString() != "Success") return new SttResult("", null);

        var best = root.GetProperty("NBest")[0];
        List<PronouncedWord>? words = null;
        if (assess && best.TryGetProperty("Words", out var ws))
        {
            words = [];
            foreach (var w in ws.EnumerateArray())
            {
                // Two response shapes exist: Words[].PronunciationAssessment.AccuracyScore and Words[].AccuracyScore
                double? acc = w.TryGetProperty("PronunciationAssessment", out var pa) && pa.TryGetProperty("AccuracyScore", out var a1)
                    ? a1.GetDouble() : w.TryGetProperty("AccuracyScore", out var a2) ? a2.GetDouble() : null;
                if (acc is { } v && w.TryGetProperty("Word", out var word)) words.Add(new PronouncedWord(word.GetString() ?? "", v));
            }
        }
        return new SttResult(best.GetProperty("Display").GetString() ?? "",
                             best.TryGetProperty("Confidence", out var c) ? c.GetDouble() : null, words);
    }
}

/// <summary>
/// Azure neural TTS with viseme events → real phoneme-timed lip-sync.
/// Low latency: synthesizers are pooled and their WebSocket is opened ahead of time (Connection.Open), so a
/// sentence doesn't pay the TLS/handshake cost — several sentences of one reply synthesize in parallel.
/// </summary>
public sealed class AzureTextToSpeech : ITextToSpeech, IDisposable
{
    private readonly SpeechConfig _config;
    private readonly System.Collections.Concurrent.ConcurrentBag<(SpeechSynthesizer synth, Connection conn)> _pool = [];

    public AzureTextToSpeech(IOptions<AzureSpeechOptions> options)
    {
        var o = options.Value;
        _config = SpeechConfig.FromSubscription(o.Key, o.Region);
        _config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3);
        for (var i = 0; i < 2; i++) _pool.Add(Create());          // warm two connections at startup
    }

    private (SpeechSynthesizer, Connection) Create()
    {
        var synth = new SpeechSynthesizer(_config, audioConfig: null);   // null → keep audio in memory
        var conn = Connection.FromSpeechSynthesizer(synth);
        conn.Open(true);                                                  // pre-connect
        return (synth, conn);
    }

    public async Task<SpeechChunk> SynthesizeAsync(string text, string language, string voice, string emotion, CancellationToken ct)
    {
        var (synth, conn) = _pool.TryTake(out var pooled) ? pooled : Create();
        var cues = new List<VisemeCue>();
        void OnViseme(object? _, SpeechSynthesisVisemeEventArgs e)
        {
            lock (cues) cues.Add(new VisemeCue((int)(e.AudioOffset / 10_000), AzureVisemeToShape((int)e.VisemeId)));
        }
        synth.VisemeReceived += OnViseme;
        try
        {

        var ssml = $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="{language}">
              <voice name="{SecurityElement.Escape(voice)}"><prosody {Prosody(emotion)}>{SecurityElement.Escape(text)}</prosody></voice>
            </speak>
            """;
        using var reg = ct.Register(() => _ = synth.StopSpeakingAsync());
        using var result = await synth.SpeakSsmlAsync(ssml);
        if (result.Reason != ResultReason.SynthesizingAudioCompleted)
        {
            var details = SpeechSynthesisCancellationDetails.FromResult(result);
            throw new InvalidOperationException($"Azure TTS failed: {details.Reason} {details.ErrorDetails}");
        }
        return new SpeechChunk(Convert.ToBase64String(result.AudioData), "audio/mpeg",
                               (int)result.AudioDuration.TotalMilliseconds, cues.OrderBy(c => c.OffsetMs).ToList());
        }
        finally
        {
            synth.VisemeReceived -= OnViseme;
            if (_pool.Count < 6) _pool.Add((synth, conn)); else { conn.Dispose(); synth.Dispose(); }
        }
    }

    public void Dispose()
    {
        while (_pool.TryTake(out var p)) { p.conn.Dispose(); p.synth.Dispose(); }
    }

    /// <summary>
    /// Emotion → prosody. uz-UZ neural voices have no "express-as" styles, so anger/sarcasm/shock are carried
    /// by rate, pitch and volume. (For en-US voices you could add mstts:express-as style="angry"/"cheerful".)
    /// </summary>
    internal static string Prosody(string emotion) => emotion switch
    {
        "angry_flame" or "angry" => "rate=\"+12%\" pitch=\"+6%\" volume=\"loud\"",
        "annoyed_cutoff" or "scolding" => "rate=\"+8%\" pitch=\"+3%\" volume=\"loud\"",
        "shocked" => "rate=\"+6%\" pitch=\"+14%\"",
        "mocking" or "skeptical" => "rate=\"-8%\" pitch=\"-6%\"",
        "laughing" => "rate=\"+8%\" pitch=\"+10%\"",
        "shy_flustered" => "rate=\"+10%\" pitch=\"+12%\" volume=\"soft\"",
        "hurt_apologetic" => "rate=\"-8%\" pitch=\"-3%\" volume=\"soft\"",
        "encouraging" => "pitch=\"+5%\"",
        "thinking" or "teaching" => "rate=\"-4%\"",
        _ => "rate=\"0%\"",
    };

    /// <summary>Azure's 22 viseme IDs (SAPI set) collapsed into the avatar's 9 mouth shapes.</summary>
    internal static string AzureVisemeToShape(int id) => id switch
    {
        0 => "rest",
        1 or 2 or 9 or 11 => "A",
        3 or 8 or 10 or 13 => "O",
        4 or 5 or 12 or 20 => "E",
        6 or 15 => "I",
        7 or 16 => "U",
        14 or 17 or 19 => "L",
        18 => "FV",
        21 => "MBP",
        _ => "E",
    };
}
