namespace PipelineProcessor.Persistence.Entities;

public class Anomaly
{
    public long Id { get; set; }

    public string RunId { get; set; } = string.Empty;

    public int RowNumber { get; set; }

    public string VendorFeatureId { get; set; } = string.Empty;

    public double DistanceM { get; set; }

    public double Lat { get; set; }

    public double Lon { get; set; }

    public double DepthPct { get; set; }

    public double LengthMm { get; set; }

    // navigation property
    public IliRun Run { get; set; } = null!;

    public ICollection<AnomalyResult> Results { get; set; } = new List<AnomalyResult>();
}
