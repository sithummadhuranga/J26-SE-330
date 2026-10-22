// A stored figure that a delivered passage cross-references in the guideline text (rag.figure_links, decision 44).

namespace WoundRag.Core.Composition;

public sealed record LinkedFigure(string FigureId, string Label, string SubGuideline, int PdfIndex, string Caption, IReadOnlyList<string> LinkedChunkIds);
