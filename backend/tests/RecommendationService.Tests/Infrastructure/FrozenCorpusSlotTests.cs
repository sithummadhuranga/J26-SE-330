// The slot words the Core tests assume are the ones iwgdf-2023.r2 really stores, and every contract value of the
// three tri-state fields, once mapped (decision 70), selects a stored query. Reads slot names and values only.

using Npgsql;
using RecommendationService.Tests.Core;

namespace RecommendationService.Tests.Infrastructure;

public class FrozenCorpusSlotTests
{
    [DatabaseFact]
    public async Task The_slot_words_in_the_tests_are_the_ones_r2_stores()
    {
        await using var conn = new NpgsqlConnection(TestDatabase.RagConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT s.key, array_agg(DISTINCT s.value ORDER BY s.value)
            FROM rag.query_vectors, jsonb_each_text(slot_values) AS s(key, value)
            WHERE corpus_version = @v GROUP BY s.key
            """, conn);
        cmd.Parameters.AddWithValue("v", TestDatabase.CorpusVersion);
        var stored = new Dictionary<string, string[]>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                stored[reader.GetString(0)] = reader.GetFieldValue<string[]>(1);

        Assert.Equal(SlotVocabularyTests.R2Words.Keys.Order(StringComparer.Ordinal), stored.Keys.Order(StringComparer.Ordinal));
        foreach (var (slot, words) in SlotVocabularyTests.R2Words)
            Assert.Equal(words.Order(StringComparer.Ordinal), stored[slot].Order(StringComparer.Ordinal));
    }
}
