using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplication2.DbContexts;
using WebApplication2.Entities;

namespace WebApplication2.Controllers;

[ApiController]
[Route("[controller]")]
public class CalculationResultController : ControllerBase
{
    private readonly ApplicationDbContext _dbContext;

    public CalculationResultController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("{id}", Name = "GetCalculationResult")]
    public async Task<IActionResult> Index(string id)
    {
        var geometry = await _dbContext.Geometries
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        if (geometry is null)
        {
            return NotFound(new { status = "notFound" });
        }

        return geometry.Status switch
        {
            // if not done yet, tell the client that the calculation is still processing
            CalculationStatus.Processing => Accepted(new { status = "processing" }),
            // the calculation will never produce a result; hand the client the reason
            CalculationStatus.Failed => UnprocessableEntity(new
            {
                status = "failed",
                error = geometry.Error
            }),
            _ => Content(geometry.Data ?? string.Empty, "application/geo+json")
        };
    }

    [HttpGet("{id}/status")]
    public async Task<IActionResult> Status(string id)
    {
        var geometry = await _dbContext.Geometries
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        if (geometry is null)
        {
            return NotFound(new { status = "notFound" });
        }

        var status = geometry.Status switch
        {
            CalculationStatus.Processing => "processing",
            CalculationStatus.Failed => "failed",
            _ => "completed"
        };

        return Ok(new
        {
            status,
            error = geometry.Status == CalculationStatus.Failed ? geometry.Error : null
        });
    }
}
