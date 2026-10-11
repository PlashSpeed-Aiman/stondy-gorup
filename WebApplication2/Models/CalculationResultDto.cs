namespace WebApplication2.Models;

public class CalculationResultDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GeometryData { get; set;}
}