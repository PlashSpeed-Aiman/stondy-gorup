using Hangfire;
using Microsoft.AspNetCore.Mvc;
using WebApplication2.Services;

namespace WebApplication2.Controllers;

[ApiController]
[Route("[controller]")]
public class FileController : ControllerBase
{
    private readonly IFileStorage _fileStorage;

    public FileController(IFileStorage fileStorage)
    {
        _fileStorage = fileStorage;
    }
    
    [HttpPost]
    public ActionResult<Object> Upload(IFormFile file)
    {
        using var memoryStream = new MemoryStream();

        file.CopyTo(memoryStream);

        var filePath = _fileStorage.Write(memoryStream.ToArray());
        if (filePath is null)
        {
            return BadRequest("File upload failed");
        }
        BackgroundJob.Enqueue<ExcelProcessingJob>(job => job.Process(filePath));
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