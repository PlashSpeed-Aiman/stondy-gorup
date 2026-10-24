using System.Text.Json.Serialization;

namespace PipelineProcessor.Models;

public class AnomalyDto
{
    public long Id { get; set; }

    public int RowNumber { get; set; }

    public string VendorFeatureId { get; set; } = string.Empty;

    public double DistanceM { get; set; }

    public double Lat { get; set; }

    public double Lon { get; set; }

    public double DepthPct { get; set; }

    public double LengthMm { get; set; }

    public double? FailurePressureMpa { get; set; }

    public double? Erf { get; set; }

    public double? GrowthMmPerYear { get; set; }

    public double? RemainingLifeYears { get; set; }
}

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; set; } = Array.Empty<T>();

    public int Page { get; set; }

    public int PageSize { get; set; }

    public int TotalCount { get; set; }
}

public class GeoJsonFeatureCollection
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "FeatureCollection";

    [JsonPropertyName("features")]
    public List<GeoJsonFeature> Features { get; set; } = new();
}

public class GeoJsonFeature
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Feature";

    [JsonPropertyName("geometry")]
    public GeoJsonPoint Geometry { get; set; } = new();

    [JsonPropertyName("properties")]
    public AnomalyDto Properties { get; set; } = new();
}

public class GeoJsonPoint
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Point";

    /// <summary>GeoJSON order: longitude, latitude.</summary>
    [JsonPropertyName("coordinates")]
    public double[] Coordinates { get; set; } = Array.Empty<double>();
}
