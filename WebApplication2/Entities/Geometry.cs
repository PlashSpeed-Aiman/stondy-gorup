namespace WebApplication2.Entities;

public class Geometry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>
    /// GeoJson data.
    /// </summary>
    public string? Data { get; set; }
}