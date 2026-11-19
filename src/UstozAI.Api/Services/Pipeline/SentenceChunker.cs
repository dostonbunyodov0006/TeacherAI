using System.Text;
using System.Text.RegularExpressions;

namespace UstozAI.Api.Services.Pipeline;

/// <summary>
/// Turns a token stream into speakable sentences so TTS can start on sentence 1 while the LLM is
/// still writing sentence 2. Also extracts the leading [emotion] tag the persona emits.
/// </summary>
public partial class SentenceChunker
{
    private readonly StringBuilder _buf = new();
    private bool _tagResolved;
    public string? Emotion { get; private set; }

    private const int MinSentence = 24;      // don't TTS tiny fragments like "Ha."
    private const int ForceSplitAt = 220;    // very long sentence → split at a comma

    /// <summary>Feed a delta; returns zero or more complete sentences.</summary>
    public IEnumerable<string> Push(string delta)
    {
        _buf.Append(delta);

        if (!_tagResolved)
        {
            var s = _buf.ToString().TrimStart();
            if (s.Length == 0) yield break;
            if (s[0] != '[') _tagResolved = true;
            else
            {
                var close = s.IndexOf(']');
                if (close < 0) { if (s.Length < 24) yield break; _tagResolved = true; }
                else
                {
                    Emotion = s[1..close].Trim().ToLowerInvariant();
                    _buf.Clear().Append(s[(close + 1)..]);
                    _tagResolved = true;
                }
            }
        }

        while (TryTake(out var sentence)) yield return sentence;
    }

    /// <summary>Flush whatever remains at end of stream.</summary>
    public string? Flush()
    {
        var rest = Clean(_buf.ToString());
        _buf.Clear();
        return rest.Length > 0 ? rest : null;
    }

    private bool TryTake(out string sentence)
    {
        var text = _buf.ToString();
        foreach (Match m in Boundary().Matches(text))
        {
            var end = m.Index + m.Length;
            if (end < MinSentence) continue;
            sentence = Clean(text[..end]);
            _buf.Remove(0, end);
            return sentence.Length > 0;
        }
        if (text.Length > ForceSplitAt)
        {
            var comma = text.LastIndexOf(", ", ForceSplitAt, StringComparison.Ordinal);
            if (comma > MinSentence)
            {
                sentence = Clean(text[..(comma + 1)]);
                _buf.Remove(0, comma + 1);
                return true;
            }
        }
        sentence = "";
        return false;
    }

    // Strip anything that would be read aloud literally (markdown residue).
    private static string Clean(string s) => Markdown().Replace(s, "").Trim();

    [GeneratedRegex(@"[.!?…]+[""»)]?(\s|$)|\n+")]
    private static partial Regex Boundary();

    // markdown residue only — keep "C#", "F#", "#1" etc. (a '#' is dropped only as a heading marker: "## Title")
    [GeneratedRegex(@"[*_`>]+|(?<![\w#])#+(?=\s)")]
    private static partial Regex Markdown();
}
