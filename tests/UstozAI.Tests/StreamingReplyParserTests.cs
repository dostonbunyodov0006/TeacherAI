using UstozAI.Api.Services.Mentor;

namespace UstozAI.Tests;

public class StreamingReplyParserTests
{
    private const string Reply = """
        {"emotion":"teaching","rageLevel":3,"shyLevel":0,"userMood":"neutral",
         "eyeAndFaceCues":[{"cue":"brow_raise","segment":0,"at":0.5}],"pivotToTopic":"Present Perfect",
         "speechSegments":[
           {"type":"speech","emotion":"teaching","text":"Qavs { va } hamda \"qo'shtirnoq\" bor gap.","intensity":1},
           {"type":"laugh_sfx","emotion":"laughing","text":"Xa-xa","intensity":3},
           {"type":"speech","emotion":"teaching","text":"Bu matnda \"speechSegments\": [ so'zi bor.","intensity":1}
         ],
         "safety":{"category":"none","note":""},
         "imageRequest":{"action":"none","prompt":"","count":1,"sourceImageIndex":0},
         "boardContent":{"show":true,"type":"grammar","title":"T","markdownBody":"a } b","highlightWords":["a"]},
         "quiz":{"active":false,"question":"","expectedAnswer":"","timeLimitSeconds":10},
         "quizResult":{"graded":false,"correct":false,"feedback":""},
         "mistakes":[],"homeworkUpdates":[],"assignHomework":[],"progressUpdates":[],"testResults":[]}
        """;

    [Theory]
    [InlineData(1)] [InlineData(3)] [InlineData(7)] [InlineData(64)] [InlineData(100000)]
    public void Parses_identically_for_any_chunking(int chunk)
    {
        var p = new StreamingReplyParser();
        MentorHeader? header = null;
        var segs = new List<SpeechSegment>();
        BoardContent? board = null;
        for (var i = 0; i < Reply.Length; i += chunk)
        {
            var (h, s) = p.Push(Reply.Substring(i, Math.Min(chunk, Reply.Length - i)));
            header ??= h;
            segs.AddRange(s);
            board ??= p.TryTakeObject<BoardContent>("boardContent");
        }
        Assert.NotNull(header);
        Assert.Equal("teaching", header!.Emotion);
        Assert.Equal(3, header.RageLevel);
        Assert.Equal("Present Perfect", header.PivotToTopic);
        Assert.Single(header.EyeAndFaceCues);
        Assert.Equal(3, segs.Count);
        Assert.Contains("{ va }", segs[0].Text);
        Assert.Equal("laugh_sfx", segs[1].Type);
        Assert.Contains("speechSegments", segs[2].Text);
        Assert.NotNull(board);
        Assert.Equal("a } b", board!.MarkdownBody);
        var full = p.Finish();
        Assert.NotNull(full);
        Assert.Equal(3, full!.SpeechSegments.Count);
    }

    [Fact]
    public void Truncated_reply_keeps_segments_already_emitted()
    {
        var cut = Reply[..Reply.IndexOf("\"safety\"", StringComparison.Ordinal)];   // e.g. max_tokens hit
        var p = new StreamingReplyParser();
        var (_, segs) = p.Push(cut);
        Assert.Equal(3, segs.Count);
        Assert.Null(p.Finish());            // full parse fails → pipeline must cope (actions are lost)
    }
}
