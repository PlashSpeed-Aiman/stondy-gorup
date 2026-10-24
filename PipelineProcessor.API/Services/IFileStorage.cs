namespace PipelineProcessor.Services;

/// <summary>
/// A port for storing and retrieving uploaded files.
/// </summary>
public interface IFileStorage
{
    Task<string?> WriteAsync(string key, byte[] file, CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default);
}
