using UstozAI.Api.Services.Speech;

namespace UstozAI.Tests;

public class LanguageSupportTests
{
    [Theory]
    [InlineData("Present Perfect qachon ishlatiladi?", "uz-UZ")]
    [InlineData("Ustoz, men bugun uy vazifasini qildim", "uz-UZ")]
    [InlineData("Bu so'zning ma'nosi nima?", "uz-UZ")]
    [InlineData("Kitoblarni o'qishni yaxshi ko'raman", "uz-UZ")]
    [InlineData("Как подготовиться к IELTS?", "ru-RU")]
    [InlineData("Объясни, пожалуйста, это правило", "ru-RU")]
    [InlineData("Ўзбек тилида гапирайлик", "uz-UZ")]         // Uzbek Cyrillic
    [InlineData("How do I prepare for IELTS?", "en-US")]
    [InlineData("Can you explain the present perfect, please?", "en-US")]
    [InlineData("What is the difference between since and for?", "en-US")]
    [InlineData("Teacher, onga qarang", "uz-UZ")]            // the name must not make it English
    [InlineData("Teacher, choy iching", "uz-UZ")]
    [InlineData("Teacher, посмотри налево", "ru-RU")]
    [InlineData("Teacher, look left", "en-US")]
    [InlineData("hazillashdim", "uz-UZ")]
    public void Detects_language(string text, string expected) => Assert.Equal(expected, LanguageSupport.Detect(text));

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("12345")]
    [InlineData("Teacher")]                                  // just the name → undecidable → session language
    public void Undecidable_returns_null(string text) => Assert.Null(LanguageSupport.Detect(text));

    [Theory]
    [InlineData("ru-RU", "ru-RU-DmitryNeural")]
    [InlineData("en-US", "en-US-AndrewNeural")]
    [InlineData("uz-UZ", "uz-UZ-SardorNeural")]
    [InlineData("xx", "uz-UZ-SardorNeural")]
    public void Voice_per_language(string lang, string voice) => Assert.Equal(voice, LanguageSupport.VoiceFor(lang));
}
