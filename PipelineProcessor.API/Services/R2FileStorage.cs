using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using PipelineProcessor.Models.Options;

namespace PipelineProcessor.Services;

/// <summary>
/// Adapter for <see cref="IFileStorage"/> backed by Cloudflare R2.
/// </summary>
public sealed class R2FileStorage : IFileStorage
{
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IAmazonS3 _s3Client;
    private readonly R2Options _options;

    public R2FileStorage(IAmazonS3 s3Client, IOptions<R2Options> options)
    {
        _s3Client = s3Client;
        _options = options.Value;
    }

    public async Task<string?> WriteAsync(
        string key,
        byte[] file,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(file);

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = ExcelContentType,
            DisablePayloadSigning = true
        };

        var response = await _s3Client.PutObjectAsync(request, cancellationToken);

        return response.HttpStatusCode == HttpStatusCode.OK ? key : null;
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        using var response = await _s3Client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = _options.BucketName,
                Key = key
            },
            cancellationToken);

        using var memoryStream = new MemoryStream();
        await response.ResponseStream.CopyToAsync(memoryStream, cancellationToken);

        return memoryStream.ToArray();
    }
}
