using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using WebApplication2.Models.Options;

namespace WebApplication2.Services;
/// <summary>
/// Adapter for our IFileStorage Port.
/// </summary>
public class R2FileStorage : IFileStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly R2Options _options;

    /// <summary>
    /// Constructor for R2FileStorage. All required dependencies are injected via constructor.
    /// </summary>
    /// <param name="s3Client"></param>
    /// <param name="options"></param>
    public R2FileStorage(IAmazonS3 s3Client, IOptions<R2Options> options)
    {
        _s3Client = s3Client;
        _options = options.Value;
    }
    public string? Write(byte[] file)
    {
        var key = $"{Guid.NewGuid()}";

        using var stream = new MemoryStream(file);

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = "application/octet-stream",
            DisablePayloadSigning = true
        };
        var response = _s3Client.PutObjectAsync(request).GetAwaiter().GetResult();
        if (response.HttpStatusCode == System.Net.HttpStatusCode.OK)
        {
            return key;
        }
        return null;
    }

    public byte[] Read(string path)
    {
        var response = _s3Client.GetObjectAsync(_options.BucketName, path).GetAwaiter().GetResult();

        using var responseStream = response.ResponseStream;
        using var memoryStream = new MemoryStream();

        responseStream.CopyTo(memoryStream);

        return memoryStream.ToArray();    }
}