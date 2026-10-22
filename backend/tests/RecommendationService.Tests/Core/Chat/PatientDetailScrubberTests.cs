// Follow-up chat: identifiers are removed from a question before it goes to any model
// (demo decision 48, option a). All names and numbers below are invented for the tests.

using WoundRag.Core.Chat.Privacy;

namespace RecommendationService.Tests.Core.Chat;

public class PatientDetailScrubberTests
{
    [Fact]
    public void A_question_without_identifiers_is_unchanged()
    {
        var result = PatientDetailScrubber.Scrub("Is offloading needed for a 2 cm plantar ulcer after 4 weeks?");
        Assert.Equal("Is offloading needed for a 2 cm plantar ulcer after 4 weeks?", result.Text);
        Assert.Empty(result.Removed);
    }

    [Theory]
    [InlineData("call 0771234567 today", "call [number removed] today")]
    [InlineData("call +94 77 123 4567 today", "call [number removed] today")]
    [InlineData("ref 1234567", "ref [number removed]")]
    public void Phone_numbers_and_long_digit_runs_are_removed(string question, string expected)
    {
        var result = PatientDetailScrubber.Scrub(question);
        Assert.Equal(expected, result.Text);
        Assert.Equal([PatientDetailScrubber.Number], result.Removed);
    }

    [Theory]
    [InlineData("NIC 853456789V says", "[ID removed] says")]
    [InlineData("NIC 198534567890 says", "[ID removed] says")]
    [InlineData("card 853456789V says", "card [ID removed] says")]
    [InlineData("BHT: 45-221 admitted", "[ID removed] admitted")]
    [InlineData("MRN 88213 seen", "[ID removed] seen")]
    public void Identity_and_record_numbers_are_removed(string question, string expected)
    {
        var result = PatientDetailScrubber.Scrub(question);
        Assert.Equal(expected, result.Text);
        Assert.Equal([PatientDetailScrubber.Identifier], result.Removed);
    }

    [Theory]
    [InlineData("seen on 12/03/2026 for", "seen on [date removed] for")]
    [InlineData("seen on 2026-03-12 for", "seen on [date removed] for")]
    [InlineData("seen on 12 March 2026 for", "seen on [date removed] for")]
    [InlineData("seen on March 12, 2026 for", "seen on [date removed] for")]
    public void Dates_are_removed(string question, string expected)
    {
        var result = PatientDetailScrubber.Scrub(question);
        Assert.Equal(expected, result.Text);
        Assert.Equal([PatientDetailScrubber.Date], result.Removed);
    }

    [Fact]
    public void Email_addresses_are_removed()
    {
        var result = PatientDetailScrubber.Scrub("send to kamal.p@example.com please");
        Assert.Equal("send to [email removed] please", result.Text);
        Assert.Equal([PatientDetailScrubber.Email], result.Removed);
    }

    [Theory]
    [InlineData("Mr. Kamal Perera has pain", "[name removed] has pain")]
    [InlineData("Mrs Silva has pain", "[name removed] has pain")]
    [InlineData("පෙරේරා මහතාට වේදනාව", "[name removed]ට වේදනාව")]
    [InlineData("සිල්වා මහත්මිය", "[name removed]")]
    [InlineData("திரு குமார் வலி", "[name removed] வலி")]
    [InlineData("திருமதி லதா வலி", "[name removed] வலி")]
    public void Names_after_a_title_are_removed_in_all_three_languages(string question, string expected)
    {
        var result = PatientDetailScrubber.Scrub(question);
        Assert.Equal(expected, result.Text);
        Assert.Equal([PatientDetailScrubber.Name], result.Removed);
    }

    // Typed on a phone, names and titles often lose their capitals: "Mr perera" reached the model in the live test.
    [Theory]
    [InlineData("Mr perera asks how often", "[name removed] asks how often")]
    [InlineData("mr perera asks", "[name removed] asks")]
    [InlineData("dr silva said", "[name removed] said")]
    [InlineData("MRS FERNANDO has pain", "[name removed] has pain")]
    public void A_lower_case_name_or_title_is_removed_too(string question, string expected)
    {
        var result = PatientDetailScrubber.Scrub(question);
        Assert.Equal(expected, result.Text);
        Assert.Equal([PatientDetailScrubber.Name], result.Removed);
    }

    // Ms, Miss and Master are also ordinary words (MS, "miss a dose", "master"), so they still need capitals.
    [Theory]
    [InlineData("patients who miss a dose")]
    [InlineData("ms patients with ulcers")]
    [InlineData("master the technique first")]
    public void Ordinary_words_that_look_like_titles_are_kept(string question)
    {
        Assert.Equal(question, PatientDetailScrubber.Scrub(question).Text);
    }

    [Fact]
    public void A_name_without_a_title_is_not_caught()
    {
        // The known limit (demo decision 48): the page warns before sending, and this test keeps the
        // limit visible instead of letting the scrubber look complete.
        var result = PatientDetailScrubber.Scrub("Kamal has a plantar ulcer");
        Assert.Equal("Kamal has a plantar ulcer", result.Text);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Every_kind_removed_is_reported_once_in_a_fixed_order()
    {
        var result = PatientDetailScrubber.Scrub("Mr Silva 0771234567, seen 12/03/2026, phone 0719876543");
        Assert.Equal([PatientDetailScrubber.Name, PatientDetailScrubber.Date, PatientDetailScrubber.Number], result.Removed);
    }

    [Fact]
    public void Short_clinical_numbers_are_kept()
    {
        var result = PatientDetailScrubber.Scrub("HbA1c 8.5, area 150 mm2, 6 weeks, grade 3");
        Assert.Equal("HbA1c 8.5, area 150 mm2, 6 weeks, grade 3", result.Text);
    }
}
