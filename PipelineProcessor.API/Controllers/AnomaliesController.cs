using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PipelineProcessor.Models;
using PipelineProcessor.Services;

namespace PipelineProcessor.Controllers;

[ApiController]
[Route("pipelines/{pipelineId}")]
[Tags("Anomalies")]
public class AnomaliesController : ControllerBase
{
    private const int GeoJsonLimit = 10000;

    private static readonly JsonSerializerOptions GeoJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly AnomalyQueryService _queries;

    public AnomaliesController(AnomalyQueryService queries)
    {
        _queries = queries;
    }

    [HttpGet("anomalies")]
    public async Task<IActionResult> GetAnomalies(
        string pipelineId,
        [FromQuery] double? minErf,
        [FromQuery] double? fromM,
        [FromQuery] double? toM,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _queries.PipelineExistsAsync(pipelineId, cancellationToken))
        {
            return NotFound();
        }

        var run = await _queries.ResolveRunAsync(pipelineId, runId, cancellationToken);
        if (run is null)
        {
            return Ok(new PagedResult<AnomalyDto>());
        }

        var assessmentId = await _queries.ResolveAssessmentIdAsync(run.Id, cancellationToken);

        var result = await _queries.GetAnomaliesAsync(
            run,
            assessmentId,
            minErf,
            fromM,
            toM,
            page,
            pageSize,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("anomalies.geojson")]
    public async Task<IActionResult> GetAnomaliesGeoJson(
        string pipelineId,
        [FromQuery] string? bbox,
        [FromQuery] string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _queries.PipelineExistsAsync(pipelineId, cancellationToken))
        {
            return NotFound();
        }

        if (!TryParseBoundingBox(bbox, out var minLon, out var minLat, out var maxLon, out var maxLat))
        {
            return BadRequest(new
            {
                error = "bbox must contain four numbers: minLon,minLat,maxLon,maxLat."
            });
        }

        var run = await _queries.ResolveRunAsync(pipelineId, runId, cancellationToken);
        var anomalies = run is null
            ? new List<AnomalyDto>()
            : await _queries.GetForGeoJsonAsync(
                run,
                await _queries.ResolveAssessmentIdAsync(run.Id, cancellationToken),
                minLon,
                minLat,
                maxLon,
                maxLat,
                GeoJsonLimit,
                cancellationToken);

        var featureCollection = new GeoJsonFeatureCollection
        {
            Features = anomalies.Select(anomaly => new GeoJsonFeature
            {
                Geometry = new GeoJsonPoint
                {
                    Coordinates = new[] { anomaly.Lon, anomaly.Lat }
                },
                Properties = anomaly
            }).ToList()
        };

        return Content(JsonSerializer.Serialize(featureCollection, GeoJsonOptions), "application/geo+json");
    }

    [HttpGet("digs")]
    public async Task<IActionResult> GetDigs(
        string pipelineId,
        [FromQuery] string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _queries.PipelineExistsAsync(pipelineId, cancellationToken))
        {
            return NotFound();
        }

        var run = await _queries.ResolveRunAsync(pipelineId, runId, cancellationToken);
        if (run is null)
        {
            return Ok(Array.Empty<AnomalyDto>());
        }

        var assessmentId = await _queries.ResolveAssessmentIdAsync(run.Id, cancellationToken);
        var digs = await _queries.GetDigsAsync(run, assessmentId, cancellationToken);

        return Ok(digs);
    }

    private static bool TryParseBoundingBox(
        string? bbox,
        out double? minLon,
        out double? minLat,
        out double? maxLon,
        out double? maxLat)
    {
        minLon = null;
        minLat = null;
        maxLon = null;
        maxLat = null;

        if (string.IsNullOrWhiteSpace(bbox))
        {
            return true;
        }

        var parts = bbox.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var values = new double[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(
                    parts[index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[index]))
            {
                return false;
            }
        }

        if (values[0] > values[2] || values[1] > values[3])
        {
            return false;
        }

        minLon = values[0];
        minLat = values[1];
        maxLon = values[2];
        maxLat = values[3];

        return true;
    }
}
