using UstozAI.Api.Services.Mentor;
using UstozAI.Api.Services.Pipeline;

namespace UstozAI.Tests;

public class CommandRouterTests
{
    [Theory]
    // look
    [InlineData("o'ngga qarang", "look_right")]
    [InlineData("Teacher, onga qarang", "look_right")]
    [InlineData("Посмотри направо", "look_right")]
    [InlineData("Teacher, look to the right", "look_right")]
    [InlineData("chapga qarang", "look_left")]
    [InlineData("Учитель, посмотри налево", "look_left")]
    [InlineData("look left", "look_left")]
    [InlineData("tepaga qarang", "look_up")]
    [InlineData("pastga qarang", "look_down")]
    [InlineData("Посмотри вниз", "look_down")]
    // posture
    [InlineData("o'tiring", "sit")]
    [InlineData("Teacher, otiring", "sit")]
    [InlineData("Садись", "sit")]
    [InlineData("sit down please", "sit")]
    [InlineData("yot", "lie_down")]
    [InlineData("Ложись отдохни", "lie_down")]
    [InlineData("turing", "stand")]
    [InlineData("Встань", "stand")]
    // tea
    [InlineData("choy iching", "drink_tea")]
    [InlineData("Teacher, choy ichib oling", "drink_tea")]
    [InlineData("Выпей чаю", "drink_tea")]
    [InlineData("have some tea", "drink_tea")]
    // joke → turn back to the student
    [InlineData("hazillashdim", "face_front")]
    [InlineData("шучу", "face_front")]
    [InlineData("just kidding", "face_front")]
    public void Recognises_commands_in_three_languages(string text, string action) =>
        Assert.Equal(action, CommandRouter.Match(text)?.Action);

    [Theory]
    [InlineData("Seni kim yaratgan?")]
    [InlineData("Teacher, sizni kim yasagan?")]
    [InlineData("Muallifing kim?")]
    [InlineData("Yaratuvchingiz kim, aytib bering, juda qiziq bo'lyapti menga hozir")]   // long sentence still caught
    [InlineData("Кто тебя создал?")]
    [InlineData("Teacher, кто твой создатель?")]
    [InlineData("Who made you?")]
    [InlineData("Who is your creator?")]
    public void Creator_question_shows_the_creator(string text) => Assert.Equal("show_creator", CommandRouter.Match(text)?.Action);

    [Theory]
    [InlineData("Teacher")]
    [InlineData("Ustoz!")]
    [InlineData("Тичер")]
    public void Calling_the_name_is_a_call(string text) => Assert.Equal("call", CommandRouter.Match(text)?.Kind);

    [Theory]
    [InlineData("salom")]
    [InlineData("Salom!")]
    [InlineData("Assalomu alaykum")]
    [InlineData("Teacher, salom")]
    [InlineData("Salom ustoz, qalaysiz?")]
    [InlineData("Привет")]
    [InlineData("Тичер, привет, как дела?")]
    [InlineData("Hey teacher")]
    [InlineData("Hello, how are you?")]
    public void Greetings_are_greeted_back(string text) => Assert.Equal("greet", CommandRouter.Match(text)?.Kind);

    [Theory]
    [InlineData("Salom, Present Perfect qachon ishlatiladi?")]     // greeting + real question → LLM
    [InlineData("Привет, объясни мне артикли")]
    public void Greeting_plus_question_goes_to_the_llm(string text) => Assert.Null(CommandRouter.Match(text));

    [Theory]
    [InlineData("Teacher, Present Perfect qachon ishlatiladi?")]          // name + a real question → LLM
    [InlineData("Teacher, как подготовиться к IELTS за три месяца?")]
    [InlineData("Men kecha uyda o'tirib uzoq vaqt grammatika mashqlarini bajardim va charchadim")]   // long sentence
    [InlineData("What is the difference between since and for?")]
    public void Real_questions_go_to_the_llm(string text) => Assert.Null(CommandRouter.Match(text));

    [Fact]
    public void Playful_dalbay_passes_in_strict_but_not_in_soft_or_empathy()
    {
        const string line = "Eh, dalbay! Men rostdan qarabman-a…";
        Assert.Contains("dalbay", InsultFilter.Apply(line, "strict", false, 0, out _));
        Assert.DoesNotContain("dalbay", InsultFilter.Apply(line, "soft", false, 0, out _));
        Assert.DoesNotContain("dalbay", InsultFilter.Apply(line, "savage", true, 10, out _));
        Assert.Contains("dalbayob", InsultFilter.Apply("dalbayob", "savage", false, 9, out _));   // tier-2 rules unchanged
        Assert.DoesNotContain("dalbayob", InsultFilter.Apply("dalbayob", "strict", false, 9, out _));
    }

    [Fact]
    public void Sentence_chunker_keeps_csharp_but_drops_markdown()
    {
        var c = new SentenceChunker();
        var parts = c.Push("Men C# va .NET dasturlash tilida yozilganman. ## Sarlavha **qalin** matn bu yerda turibdi.").ToList();
        if (c.Flush() is { } tail) parts.Add(tail);
        var all = string.Join(" ", parts);
        Assert.Contains("C#", all);
        Assert.DoesNotContain("##", all);
        Assert.DoesNotContain("**", all);
    }

    [Theory]
    [InlineData("tepaga", "look_up")]
    [InlineData("Tepaga qara", "look_up")]
    [InlineData("pasga", "look_down")]
    [InlineData("pastga qarang", "look_down")]
    [InlineData("onga qara", "look_right")]
    [InlineData("o'ngga", "look_right")]
    [InlineData("chapga", "look_left")]
    [InlineData("Teacher, yerga qaragin", "look_down")]
    public void Bare_directions_are_understood(string text, string action) => Assert.Equal(action, CommandRouter.Match(text)?.Action);
}
