// One wound-area measurement: when it was captured (device clock), its area, and the pipeline versions that made it.

namespace WoundRag.Core.Cases;

public sealed record Measurement(DateTimeOffset CapturedAt, double AreaMm2, string Calibration, string Segmentation);
