namespace PipelineProcessor.Persistence.Entities;

public class ImportError
{
    public long Id { get; set; }

    public string RunId { get; set; } = string.Empty;

    public int RowNumber { get; set; }

    public string Message { get; set; } = string.Empty;

    public IliRun Run { get; set; } = null!;
}
