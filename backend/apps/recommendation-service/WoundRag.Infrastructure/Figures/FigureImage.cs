// One stored figure as served: its bytes exactly as embedded in the PDF, its media type and their SHA-256.

namespace WoundRag.Infrastructure.Figures;

public sealed record FigureImage(byte[] Bytes, string MediaType, string Sha256);
