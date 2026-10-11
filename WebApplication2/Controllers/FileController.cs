using Hangfire;
using Microsoft.AspNetCore.Mvc;
using WebApplication2.DbContexts;
using WebApplication2.Entities;
using WebApplication2.Services;

namespace WebApplication2.Controllers;

[ApiController]
[Route("[controller]")]
public class FileController : ControllerBase
{
    private readonly IFileStorage _fileStorage;
    private readonly ApplicationDbContext _dbContext;

    public FileController(IFileStorage fileStorage, ApplicationDbContext dbContext)
    {
        _fileStorage = fileStorage;
        _dbContext = dbContext;
    }

    [HttpPost]
    public async Task<ActionResult<object>> Upload(IFormFile file)
    {
        using var memoryStream = new MemoryStream();

        await file.CopyToAsync(memoryStream);

        var filePath = _fileStorage.Write(memoryStream.ToArray());
        if (filePath is null)
        {
            return BadRequest("File upload failed");
        }

        var geometry = new Geometry
        {
            Id = filePath,
            Status = CalculationStatus.Processing
        };
        _dbContext.Geometries.Add(geometry);
        await _dbContext.SaveChangesAsync();

        try
        {
            BackgroundJob.Enqueue<ExcelProcessingJob>(job => job.Process(filePath));
        }
        catch
        {
            // without a job there will never be a result; record the failure instead of leaving it processing forever
            geometry.Status = CalculationStatus.Failed;
            geometry.Error = "The processing job could not be enqueued.";
            await _dbContext.SaveChangesAsync();
            throw;
        }

        return CreatedAtRoute(
            "GetCalculationResult",
            new { id = filePath },
            new
            {
                id = filePath,
                resultUrl = Url.RouteUrl("GetCalculationResult", new { id = filePath })
            });
    }
}
