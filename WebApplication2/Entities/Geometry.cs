namespace WebApplication2.Entities;

public class Geometry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>
    /// GeoJson data.
    /// </summary>
    public string? Data { get; set; }
    public CalculationStatus Status { get; set; } = CalculationStatus.Processing;
    /// <summary>
    /// Error message populated when <see cref="Status"/> is <see cref="CalculationStatus.Failed"/>.
    /// </summary>
    public string? Error { get; set; }
}