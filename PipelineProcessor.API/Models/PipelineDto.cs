using System.Text.Json;

namespace PipelineProcessor.Models;

public class CreatePipelineDto
{
    public string Name { get; set; } = string.Empty;

    public double OuterDiameterMm { get; set; }

    public double WallThicknessMm { get; set; }

    public string? Grade { get; set; } = string.Empty;

    public double SmysMpa { get; set; }

    public double MaopMpa { get; set; }

    /// <summary>The GeoJSON route as a JSON object, matching the generated pipeline.json.</summary>
    public JsonElement? Route { get; set; }

    /// <summary>The GeoJSON route as a string, if the client already serialized it.</summary>
    public string? RouteGeoJson { get; set; }
}

public class UpdatePipelineDto : CreatePipelineDto
{
    public string Id { get; set; } = string.Empty;
}

public class PipelineDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public double OuterDiameterMm { get; set; }

    public double WallThicknessMm { get; set; }

    public string Grade { get; set; } = string.Empty;

    public double SmysMpa { get; set; }

    public double MaopMpa { get; set; }

    public string? RouteGeoJson { get; set; } = string.Empty;
}
