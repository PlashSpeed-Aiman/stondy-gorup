namespace PipelineProcessor.Persistence.Entities;

public class IliRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string PipelineId { get; set; } = string.Empty;

    public DateOnly InspectionDate { get; set; }

    public string Status { get; set; } = string.Empty;

    public int ProgressPct { get; set; }

    // FK
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }

    public Pipeline Pipeline { get; set; } = null!;

    public ICollection<Anomaly> Anomalies { get; set; } = new List<Anomaly>();

    public ICollection<ImportError> ImportErrors { get; set; } = new List<ImportError>();

    public ICollection<Assessment> Assessments { get; set; } = new List<Assessment>();
}
