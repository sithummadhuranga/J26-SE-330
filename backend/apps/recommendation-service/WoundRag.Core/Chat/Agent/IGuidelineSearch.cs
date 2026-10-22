// The search behind the search_guideline tool. The implementation applies the gate (Req(d) ⊆ Obs(x)) for the
// session's case, so whatever query the model writes, only admissible passages come back (demo decision 49).

namespace WoundRag.Core.Chat.Agent;

// Gated search of the frozen corpus for one session.
public interface IGuidelineSearch
{
    Task<IReadOnlyList<FoundPassage>> SearchAsync(string query, int limit, CancellationToken ct);

    // The passages the gate withholds for this case among the query's best matches in the whole pool, ranked as the
    // search ranks them. Code counts them for a notice.
    Task<IReadOnlyList<WithheldMatch>> WithheldAsync(string query, int limit, CancellationToken ct);
}
