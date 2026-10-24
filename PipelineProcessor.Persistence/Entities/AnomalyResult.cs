namespace PipelineProcessor.Persistence.Entities;

public class AnomalyResult
{
    public string AssessmentId { get; set; } = string.Empty;

    public long AnomalyId { get; set; }

    public double FailurePressureMpa { get; set; }

    public double Erf { get; set; }

    public long? MatchedAnomalyId { get; set; }

    public double? GrowthMmPerYear { get; set; }

    public double? RemainingLifeYears { get; set; }

    public Assessment Assessment { get; set; } = null!;

    public Anomaly Anomaly { get; set; } = null!;
}
