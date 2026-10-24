namespace PipelineProcessor.Models;

public class CreateIliRunDto
{
    public IFormFile? File { get; set; }

    public DateOnly InspectionDate { get; set; }
}

public class RunCreatedDto
{
    public string RunId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string StatusUrl { get; set; } = string.Empty;
}

public class RunStatusDto
{
    public string RunId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int ProgressPct { get; set; }

    public string? Error { get; set; }
}
