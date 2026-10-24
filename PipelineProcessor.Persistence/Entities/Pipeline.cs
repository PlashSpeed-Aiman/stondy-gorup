namespace PipelineProcessor.Persistence.Entities;

public class Pipeline
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public double OuterDiameterMm { get; set; }

    public double WallThicknessMm { get; set; }

    public string Grade { get; set; } = string.Empty;

    public double SmysMpa { get; set; }

    public double MaopMpa { get; set; }

    public string RouteGeoJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public ICollection<IliRun> Runs { get; set; } = new List<IliRun>();
}