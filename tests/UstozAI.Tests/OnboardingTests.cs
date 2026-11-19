using UstozAI.Api.Services.Pipeline;
using Xunit;

namespace UstozAI.Tests;

public class OnboardingTests
{
    [Theory]
    [InlineData("15", 15)]
    [InlineData("Men 15 yoshdaman", 15)]
    [InlineData("o'n besh yoshdaman", 15)]
    [InlineData("yigirma ikki", 22)]
    [InlineData("Мне двадцать два", 22)]
    [InlineData("I'm twenty one", 21)]
    [InlineData("salom", null)]
    [InlineData("123", null)]
    public void Age_is_parsed_from_digits_and_words(string text, int? age) => Assert.Equal(age, Onboarding.ParseAge(text));

    [Fact]
    public void Birth_year_becomes_age() => Assert.Equal(DateTime.UtcNow.Year - 2010, Onboarding.ParseAge("2010-yilda tug'ilganman"));

    [Theory]
    [InlineData("O'g'il bolaman", "male")]
    [InlineData("oʻgʻil bola", "male")]
    [InlineData("men 15 yoshdaman", null)]          // "man/men" is "I" in Uzbek, not "male"
    [InlineData("Qiz bolaman", "female")]
    [InlineData("qizman", "female")]
    [InlineData("Я девочка", "female")]
    [InlineData("мальчик", "male")]
    [InlineData("I'm a boy", "male")]
    public void Gender_is_parsed(string text, string? gender) => Assert.Equal(gender, Onboarding.Parse(text, Onboarding.AskGender).Gender);

    [Theory]
    [InlineData("Toshkentdan", "Toshkent")]
    [InlineData("Men Samarqanddanman", "Samarqand")]
    [InlineData("Farg'ona", "Farg'ona")]
    [InlineData("Я из Ташкента", "Ташкента")]
    [InlineData("I'm from Bukhara", "Bukhara")]
    public void City_is_parsed(string text, string city) => Assert.Equal(city, Onboarding.ParseCity(text));

    [Fact]
    public void Full_flow_age_gender_city()
    {
        var o = Onboarding.Advance(Onboarding.Start, Onboarding.Parse("salom", 0), false, false, false, 0);
        Assert.Equal(("intro", Onboarding.AskAge), (o.Kind, o.NextStep));
        o = Onboarding.Advance(Onboarding.AskAge, Onboarding.Parse("15", 1), false, false, false, 0);
        Assert.Equal(("next", Onboarding.AskGender), (o.Kind, o.NextStep));
        o = Onboarding.Advance(Onboarding.AskGender, Onboarding.Parse("o'g'il bolaman", 2), true, false, false, 0);
        Assert.Equal(("next", Onboarding.AskCity), (o.Kind, o.NextStep));
        o = Onboarding.Advance(Onboarding.AskCity, Onboarding.Parse("Toshkentdanman", 3), true, true, false, 0);
        Assert.Equal(("done", Onboarding.Done), (o.Kind, o.NextStep));
        Assert.Equal("Toshkent", o.Facts.City);
    }

    [Fact]
    public void Unclear_answer_is_asked_once_more_then_skipped()
    {
        var o = Onboarding.Advance(Onboarding.AskAge, Onboarding.Parse("bilmadim", 1), false, false, false, 0);
        Assert.Equal(("retry", Onboarding.AskAge), (o.Kind, o.NextStep));
        o = Onboarding.Advance(Onboarding.AskAge, Onboarding.Parse("bilmadim", 1), false, false, false, 1);
        Assert.Equal(Onboarding.AskGender, o.NextStep);
    }

    [Fact]
    public void Keyinroq_ends_the_talk() =>
        Assert.Equal("skip", Onboarding.Advance(Onboarding.AskAge, Onboarding.Parse("keyinroq", 1), false, false, false, 0).Kind);

    [Fact]
    public void Answering_several_things_at_once_skips_ahead()
    {
        var o = Onboarding.Advance(Onboarding.AskAge, Onboarding.Parse("15 yoshli o'g'il bolaman", 1), false, false, false, 0);
        Assert.Equal(Onboarding.AskCity, o.NextStep);
    }

    [Fact]
    public void Kids_get_no_insults_in_the_brief() =>
        Assert.Contains("BOLA", Onboarding.RenderStudent("Ali", "male", 11, "Toshkent"));

    [Theory]
    [InlineData("Men o'g'il bolaman", "male")]
    [InlineData("o'g'il bolaman", "male")]
    [InlineData("qizman", "female")]
    [InlineData("Я парень", "male")]
    [InlineData("Men bugun qiz bola bilan uchrashdim va gaplashdim", null)]   // a story, not a statement about me
    public void Gender_can_be_corrected_later(string text, string? g) => Assert.Equal(g, Onboarding.GenderStatement(text));
}
