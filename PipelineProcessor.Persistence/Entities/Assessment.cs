namespace PipelineProcessor.Persistence.Entities;

public class Assessment
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string RunId { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public double MaopMpaUsed { get; set; }

    public DateTime CreatedAt { get; set; }

    public IliRun Run { get; set; } = null!;

    public ICollection<AnomalyResult> Results { get; set; } = new List<AnomalyResult>();
}
