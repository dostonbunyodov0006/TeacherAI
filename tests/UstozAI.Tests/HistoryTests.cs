using UstozAI.Api.Services;
using UstozAI.Api.Services.Llm;
using Xunit;

namespace UstozAI.Tests;

public class HistoryTests
{
    private static ChatTurn U(string t) => new(true, t);
    private static ChatTurn A(string t) => new(false, t);
    private static string Shape(List<ChatTurn> h) => string.Concat(h.Select(t => t.IsUser ? "U" : "A"));

    [Fact]
    public void Window_starting_with_teacher_is_trimmed_to_start_with_the_student()
    {
        var h = ClaudeLlmService.NormalizeHistory([U("1"), A("1"), U("2"), A("2"), U("3")], 4);   // window = A U A U
        Assert.Equal("UAU", Shape(h));
        Assert.Equal("2", h[0].Text);
    }

    [Fact]
    public void Lost_reply_gets_a_placeholder_so_roles_alternate()
    {
        var h = ClaudeLlmService.NormalizeHistory([U("a"), U("b"), A("x"), U("c")], 40);
        Assert.Equal("UAUAU", Shape(h));
    }

    [Fact]
    public void Two_teacher_lines_in_a_row_are_joined()
    {
        var h = ClaudeLlmService.NormalizeHistory([A("roast"), U("hi"), A("one"), A("two"), U("ok")], 40);
        Assert.Equal("UAU", Shape(h));
        Assert.Equal("one two", h[1].Text);
    }

    [Fact]
    public void Always_ends_with_the_student() =>
        Assert.Equal("U", Shape(ClaudeLlmService.NormalizeHistory([U("q"), A("a")], 40)));
}
