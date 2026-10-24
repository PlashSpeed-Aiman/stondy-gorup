using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Models;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;

namespace PipelineProcessor.Controllers;

[ApiController]
[Route("pipelines")]
[Tags("Pipelines")]
public class PipelinesController : ControllerBase
{
    private readonly PipelineDbContext _pipelineDbContext;

    public PipelinesController(PipelineDbContext pipelineDbContext)
    {
        _pipelineDbContext = pipelineDbContext;
    }

    [HttpPost]
    public async Task<IActionResult> CreatePipeline(
        CreatePipelineDto pipelineToCreate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipelineToCreate.Name))
        {
            return BadRequest(new { error = "name is required." });
        }

        if (pipelineToCreate.OuterDiameterMm <= 0 || pipelineToCreate.WallThicknessMm <= 0)
        {
            return BadRequest(new { error = "outerDiameterMm and wallThicknessMm must be greater than zero." });
        }

        if (pipelineToCreate.WallThicknessMm >= pipelineToCreate.OuterDiameterMm / 2)
        {
            return BadRequest(new { error = "wallThicknessMm must be less than half the outer diameter." });
        }

        if (pipelineToCreate.SmysMpa <= 0 || pipelineToCreate.MaopMpa <= 0)
        {
            return BadRequest(new { error = "smysMpa and maopMpa must be greater than zero." });
        }

        var newPipeline = new Pipeline
        {
            Name = pipelineToCreate.Name.Trim(),
            OuterDiameterMm = pipelineToCreate.OuterDiameterMm,
            WallThicknessMm = pipelineToCreate.WallThicknessMm,
            Grade = pipelineToCreate.Grade?.Trim() ?? string.Empty,
            SmysMpa = pipelineToCreate.SmysMpa,
            MaopMpa = pipelineToCreate.MaopMpa,
            RouteGeoJson = ReadRouteGeoJson(pipelineToCreate),
            CreatedAt = DateTime.UtcNow
        };

        _pipelineDbContext.Pipelines.Add(newPipeline);
        await _pipelineDbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetPipeline), new { pipelineId = newPipeline.Id }, newPipeline);
    }

    [HttpPut]
    public IActionResult UpdatePipeline(UpdatePipelineDto pipelineToUpdate)
    {
        return Ok();
    }

    [HttpGet("{pipelineId}")]
    public async Task<IActionResult> GetPipeline(
        string pipelineId,
        CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineDbContext.Pipelines
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == pipelineId, cancellationToken);

        if (pipeline is null)
        {
            return NotFound();
        }

        return Ok(pipeline);
    }

    private static string ReadRouteGeoJson(CreatePipelineDto pipeline)
    {
        if (!string.IsNullOrWhiteSpace(pipeline.RouteGeoJson))
        {
            return pipeline.RouteGeoJson;
        }

        if (pipeline.Route is not { ValueKind: not System.Text.Json.JsonValueKind.Null and not System.Text.Json.JsonValueKind.Undefined } route)
        {
            return string.Empty;
        }

        return route.ValueKind == System.Text.Json.JsonValueKind.String
            ? route.GetString() ?? string.Empty
            : route.GetRawText();
    }
}
