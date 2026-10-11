using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApplication2.DbContexts;

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

    [HttpGet("{id}",  Name = "GetCalculationResult")]
    public async Task<IActionResult> Index(string id)
    {
        var geometry = await _dbContext.Geometries
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id);

        if (geometry is null)
        {
            // if not done yet, tell the client that the calculation is still processing
            return Accepted(new { status = "processing" });
        }

        return Content(geometry.Data ?? string.Empty, "application/geo+json");
    }

    [HttpGet("{id}/status")]
    public async Task<IActionResult> Status(string id)
    {
        var isDone = await _dbContext.Geometries.AnyAsync(g => g.Id == id);

        return Ok(new { status = isDone ? "completed" : "processing" });
    }
}