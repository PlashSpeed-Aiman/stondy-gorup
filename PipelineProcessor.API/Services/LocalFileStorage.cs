namespace PipelineProcessor.Services;

/// <summary>
/// Development adapter for <see cref="IFileStorage"/> that writes uploads to a local directory.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootPath;

    public LocalFileStorage(string rootPath)
    {
        _rootPath = rootPath;
    }

    public async Task<string?> WriteAsync(string key, byte[] file, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_rootPath);

        await File.WriteAllBytesAsync(PathFor(key), file, cancellationToken);

        return key;
    }

    public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        return File.ReadAllBytesAsync(PathFor(key), cancellationToken);
    }

    private string PathFor(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Path.GetFileName(key) != key)
        {
            throw new ArgumentException("Invalid file key.", nameof(key));
        }

        return Path.Combine(_rootPath, $"{key}.xlsx");
    }
}
