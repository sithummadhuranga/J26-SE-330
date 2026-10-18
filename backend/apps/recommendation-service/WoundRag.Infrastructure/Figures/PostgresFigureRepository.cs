// Linked figures from rag.figures and rag.figure_links (Sql/linked_figures.sql).

using Dapper;
using Npgsql;
using WoundRag.Core.Composition;

namespace WoundRag.Infrastructure.Figures;

public sealed class PostgresFigureRepository(NpgsqlDataSource db) : IFigureRepository
{
    private static readonly string Sql = SqlFiles.Read("linked_figures.sql");

    public async Task<IReadOnlyList<LinkedFigure>> LinkedAsync(string version, IReadOnlyCollection<string> chunkIds, CancellationToken ct)
    {
        if (chunkIds.Count == 0) return [];
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(Sql, new { version, ids = chunkIds.ToArray() }, cancellationToken: ct));
        return [.. rows.Select(r => new LinkedFigure(r.FigureId, r.Label, r.SubGuideline, r.PdfIndex, r.Caption, r.LinkedChunkIds))];
    }

    // Dapper maps text[] to an array property, not to a record's list parameter.
    private sealed class Row
    {
        public string FigureId { get; init; } = "";
        public string Label { get; init; } = "";
        public string SubGuideline { get; init; } = "";
        public int PdfIndex { get; init; }
        public string Caption { get; init; } = "";
        public string[] LinkedChunkIds { get; init; } = [];
    }
}
