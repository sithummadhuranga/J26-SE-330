// Follow-up chat: which language a clinician's question is in (demo decision 47). Detection is
// by Unicode script only; an explicit choice from the clinician always wins over detection.

using WoundRag.Core.Chat;

namespace RecommendationService.Tests.Core.Chat;

public class ChatLanguageTests
{
    [Theory]
    [InlineData("What does the guideline say about offloading?", ChatLanguage.English)]
    [InlineData("පාදයේ තුවාලයට කුමක් කළ යුතුද?", ChatLanguage.Sinhala)]
    [InlineData("காயத்துக்கு என்ன செய்ய வேண்டும்?", ChatLanguage.Tamil)]
    public void The_script_with_the_most_letters_decides(string question, string expected)
    {
        Assert.Equal(expected, ChatLanguage.Detect(question));
    }

    [Fact]
    public void English_clinical_terms_inside_a_Sinhala_question_keep_it_Sinhala()
    {
        Assert.Equal(ChatLanguage.Sinhala, ChatLanguage.Detect("offloading ගැන මාර්ගෝපදේශය කියන්නේ මොනවාද?"));
    }

    [Fact]
    public void Romanised_Sinhala_is_read_as_English()
    {
        // A known limit, recorded in demo decision 47: Latin letters are Latin letters. The clinician
        // can still pick Sinhala explicitly.
        Assert.Equal(ChatLanguage.English, ChatLanguage.Detect("offloading gana mokakda kiyanne"));
    }

    [Fact]
    public void A_question_with_no_letters_is_English()
    {
        Assert.Equal(ChatLanguage.English, ChatLanguage.Detect("??? 123"));
    }

    [Theory]
    [InlineData("auto", "පාදය", ChatLanguage.Sinhala)]
    [InlineData("ta", "What about pulses?", ChatLanguage.Tamil)]
    [InlineData("si", "What about pulses?", ChatLanguage.Sinhala)]
    [InlineData("en", "පාදය", ChatLanguage.English)]
    public void An_explicit_choice_wins_over_detection(string choice, string question, string expected)
    {
        Assert.Equal(expected, ChatLanguage.Resolve(choice, question));
    }

    // Live test, 19 Oct 2026: "tell me in sinhala pls" typed in English letters was answered in English.
    [Theory]
    [InlineData("what is offloading tell me in sinhala pls", ChatLanguage.Sinhala)]
    [InlineData("can you tell me about footwear in sinhala pls", ChatLanguage.Sinhala)]
    [InlineData("Explain offloading in Sinhalese", ChatLanguage.Sinhala)]
    [InlineData("answer in tamil please: what about footwear?", ChatLanguage.Tamil)]
    [InlineData("footwear ගැන සිංහලෙන් කියන්න", ChatLanguage.Sinhala)]
    [InlineData("footwear about தமிழில் please", ChatLanguage.Tamil)]
    [InlineData("Are Sinhala patients treated differently?", ChatLanguage.English)]
    public void On_auto_a_language_asked_for_in_the_question_is_used(string question, string expected)
    {
        Assert.Equal(expected, ChatLanguage.Resolve(ChatLanguage.Auto, question));
    }

    [Fact]
    public void The_choice_in_the_menu_still_wins_over_a_request_in_the_text()
    {
        Assert.Equal(ChatLanguage.English, ChatLanguage.Resolve(ChatLanguage.English, "footwear in sinhala pls"));
    }

    [Theory]
    [InlineData("what is offloading tell me in sinhala pls", "what is offloading")]
    [InlineData("can you tell me about footwear in sinhala pls", "can you tell me about footwear")]
    [InlineData("answer in tamil please: what about footwear?", "what about footwear?")]
    [InlineData("Are Sinhala patients treated differently?", "Are Sinhala patients treated differently?")]
    [InlineData("in sinhala please", "in sinhala please")]
    public void The_request_itself_is_taken_out_of_the_question_the_model_sees(string question, string expected)
    {
        Assert.Equal(expected, ChatLanguage.WithoutRequest(question));
    }

    [Fact]
    public void An_unknown_choice_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ChatLanguage.Resolve("fr", "question"));
    }

    [Theory]
    [InlineData("offloading device for plantar ulcer", true)]
    [InlineData("පාදය offloading", false)]
    [InlineData("காயம்", false)]
    public void A_search_query_must_be_in_Latin_script(string query, bool accepted)
    {
        // Measured 18 Oct 2026: Qwen 3.7 Flash wrote its search query in Sinhala although the
        // tool asked for English. The lexical arm is English-stemmed, so code checks it.
        Assert.Equal(accepted, ChatLanguage.IsLatinOnly(query));
    }
}
