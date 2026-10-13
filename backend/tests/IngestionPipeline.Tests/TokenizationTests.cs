// PyText, the WordPiece counter and A1's percentile, on a tiny made-up vocabulary so they run everywhere.

using IngestionPipeline.Steps;
using WoundRag.Shared.Text;
using WoundRag.Shared.Tokenization;

namespace IngestionPipeline.Tests;

public class TokenizationTests
{
    private static readonly WordPieceCounter Counter = new(
        ["▁ulcer", "▁off", "load", "ing", "▁foot", "▁-", "▁(", "▁)", "▁naive"]);

    [Fact]
    public void Greedy_longest_match_splits_a_word_into_start_and_continuation_pieces() =>
        Assert.Equal(["▁off", "load", "ing"], Counter.Tokenize("offloading"));

    [Fact]
    public void Text_is_lowercased_and_accents_stripped_before_lookup() =>
        Assert.Equal(["▁ulcer", "▁naive"], Counter.Tokenize("ULCER Naïve"));

    [Fact]
    public void Punctuation_is_split_off_as_its_own_word() =>
        Assert.Equal(["▁foot", "▁-", "▁ulcer", "▁(", "▁)"], Counter.Tokenize("foot-ulcer()"));

    [Fact]
    public void An_unknown_word_is_one_UNK_token_not_zero() =>
        Assert.Equal(["[UNK]"], Counter.Tokenize("xyz"));

    [Fact]
    public void A_word_over_100_characters_is_one_UNK_token() =>
        Assert.Equal(1, Counter.Count(new string('a', 101)));

    [Fact]
    public void Special_tokens_are_added_only_on_request()
    {
        Assert.Equal(3, Counter.Count("offloading"));
        Assert.Equal(5, Counter.Count("offloading", withSpecialTokens: true));
    }

    [Fact]
    public void PyText_treats_U001C_to_U001F_as_whitespace_like_Python()
    {
        Assert.Equal(["a", "b", "c"], PyText.Split("a\u001Cb\u001Fc"));
        Assert.Equal("a b", PyText.NormalizeSpaces("  a \t\n b\u001D "));
        Assert.Equal("x", PyText.Strip("\u001E x  "));
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(90, 9)]
    [InlineData(95, 10)]   // ceil(0.95 × 10) = 10 → the largest value
    [InlineData(100, 10)]
    [InlineData(1, 1)]
    public void Percentile_is_nearest_rank(double pct, int expected) =>
        Assert.Equal(expected, A1Recon.Percentile([10, 9, 8, 7, 6, 5, 4, 3, 2, 1], pct));
}
