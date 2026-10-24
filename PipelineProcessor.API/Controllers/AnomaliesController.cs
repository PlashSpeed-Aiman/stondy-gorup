using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PipelineProcessor.Models;
using PipelineProcessor.Services;

namespace PipelineProcessor.Controllers;

// ============================================================================
// AnomaliesController
// ----------------------------------------------------------------------------
// This controller exposes three different views of the same underlying data:
//
//   GET /pipelines/{id}/anomalies           -> table/list view with filters
//   GET /pipelines/{id}/anomalies.geojson   -> map layer
//   GET /pipelines/{id}/digs                -> repair list, most urgent first
//
// All three use AnomalyQueryService, which owns the rule "use the latest
// completed run and its latest assessment". The controller only translates
// query parameters and shapes HTTP responses, so it stays thin.
//
// Why return GeoJSON at all?
// --------------------------
// GeoJSON is the standard interchange format for geographic data. Map clients
// such as Leaflet, Mapbox GL, OpenLayers, QGIS and many GIS tools can consume a
// FeatureCollection directly. If we returned a plain array of anomaly rows, the
// client would have to convert each row into a point and invent its own format
// first. By emitting GeoJSON we let the client say "add this URL as a map layer"
// and get points, coordinates and popup properties in one standard response.
//
// The ordinary /anomalies endpoint still exists because a table, spreadsheet
// export or mobile list does not want geographic envelopes; it wants simple
// paged rows. Same data, different representation, different consumer.
// ============================================================================

[ApiController]
[Route("pipelines/{pipelineId}")]
[Tags("Anomalies")]
public class AnomaliesController : ControllerBase
{
    // A map request covers a bounding box, and a long pipeline can contain tens
    // of thousands of defects. This cap protects the server and the browser from
    // enormous GeoJSON responses. If an area has more than 10,000 points, the
    // client should zoom in (smaller bbox) or cluster the points.
    private const int GeoJsonLimit = 10000;

    // System.Text.Json options for the GeoJSON response. JsonSerializerDefaults
    // .Web gives camelCase property names, matching the rest of the API.
    //
    // We serialize manually instead of using Ok(...) because a GeoJSON response
    // should advertise the standard media type application/geo+json, not the
    // generic application/json.
    private static readonly JsonSerializerOptions GeoJsonOptions =
        new(JsonSerializerDefaults.Web);

    // The query service is injected. It knows the "latest run/assessment" rule,
    // so this controller does not repeat that logic in three actions.
    private readonly AnomalyQueryService _queries;

    public AnomaliesController(AnomalyQueryService queries)
    {
        _queries = queries;
    }

    // ------------------------------------------------------------------------
    // GET /pipelines/{pipelineId}/anomalies
    // ------------------------------------------------------------------------
    // The general-purpose list endpoint. Typical consumers are tables, reports
    // and exports. Filters:
    //   minErf  -> only defects with ERF >= this value (danger threshold)
    //   fromM   -> chainage lower bound, in metres
    //   toM     -> chainage upper bound, in metres
    //   page    -> 1-based page number
    //   pageSize-> rows per page (the service caps it)
    //   runId   -> optional override; otherwise the latest completed run is used
    //
    // Example: ?minErf=1&fromM=1000&toM=5000&page=1&pageSize=100
    // ------------------------------------------------------------------------
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
        // A pipeline that does not exist is a 404. A pipeline that exists but
        // has no completed run is not an error: the data simply has not been
        // processed yet, so the action returns an empty page below.
        if (!await _queries.PipelineExistsAsync(pipelineId, cancellationToken))
        {
            return NotFound();
        }

        // Resolve the run before querying anomalies. With no runId, the service
        // picks the most recent Completed run by inspection date. This is the
        // "current state of the pipeline" that an engineer usually wants.
        var run = await _queries.ResolveRunAsync(pipelineId, runId, cancellationToken);
        if (run is null)
        {
            return Ok(new PagedResult<AnomalyDto>());
        }

        // Assessment results live in a separate table. An anomaly can exist
        // before it has been assessed, so assessmentId may be null. The query
        // service then returns the anomalies with their ERF fields as null.
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

        // 200 OK with a paged envelope:
        // { items: [...], page, pageSize, totalCount }
        return Ok(result);
    }

    // ------------------------------------------------------------------------
    // GET /pipelines/{pipelineId}/anomalies.geojson
    // ------------------------------------------------------------------------
    // The map-layer endpoint. It returns a GeoJSON FeatureCollection:
    //
    //   {
    //     "type": "FeatureCollection",
    //     "features": [
    //       {
    //         "type": "Feature",
    //         "geometry": { "type": "Point", "coordinates": [lon, lat] },
    //         "properties": { ...the anomaly fields... }
    //       }
    //     ]
    //   }
    //
    // Each anomaly becomes one Point feature. The geometry is what the map draws;
    // the properties are what it shows in a popup or uses to colour the point
    // (for example, red when ERF >= 1).
    //
    // The bbox parameter restricts the query to the visible map rectangle:
    //   ?bbox=minLon,minLat,maxLon,maxLat
    // Omitting bbox returns the whole pipeline (up to GeoJsonLimit features).
    // ------------------------------------------------------------------------
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

        // Parse and validate the bounding box before touching the database.
        // Invalid input is a client error, so 400 with a helpful message.
        if (!TryParseBoundingBox(bbox, out var minLon, out var minLat, out var maxLon, out var maxLat))
        {
            return BadRequest(new
            {
                error = "bbox must contain four numbers: minLon,minLat,maxLon,maxLat."
            });
        }

        // Same "latest completed run" rule as the list endpoint. If there is no
        // processed run yet, return a valid but empty FeatureCollection rather
        // than a 404: the pipeline exists, it just has no map data yet.
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

        // Build the standard GeoJSON envelope.
        var featureCollection = new GeoJsonFeatureCollection
        {
            Features = anomalies.Select(anomaly => new GeoJsonFeature
            {
                // IMPORTANT: GeoJSON coordinates are [longitude, latitude].
                // Latitude first is one of the most common GeoJSON mistakes and
                // swaps the point to the wrong side of the planet.
                Geometry = new GeoJsonPoint
                {
                    Coordinates = new[] { anomaly.Lon, anomaly.Lat }
                },

                // The whole anomaly DTO becomes the feature's properties. A map
                // client can then read properties.erf, properties.depthPct,
                // properties.vendorFeatureId, and so on without extra requests.
                Properties = anomaly
            }).ToList()
        };

        // Content(...) lets us set the GeoJSON media type explicitly. A browser
        // or GIS client can recognise the response as a map layer. The status
        // is 200 by default.
        return Content(JsonSerializer.Serialize(featureCollection, GeoJsonOptions), "application/geo+json");
    }

    // ------------------------------------------------------------------------
    // GET /pipelines/{pipelineId}/digs
    // ------------------------------------------------------------------------
    // The repair list. Unlike the map endpoint, this is deliberately filtered:
    // only defects with ERF >= 1 are included, because ERF 1 means "repair is
    // required". The query service sorts them by ERF descending and then by
    // remaining life ascending, so the most urgent excavation appears first.
    //
    // The result is a plain JSON array rather than a paged envelope because a
    // well-maintained dig list is expected to be short. If it grows large, the
    // same paging approach as /anomalies can be added later.
    // ------------------------------------------------------------------------
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
            // No completed run means no assessment yet, so no repair list.
            return Ok(Array.Empty<AnomalyDto>());
        }

        var assessmentId = await _queries.ResolveAssessmentIdAsync(run.Id, cancellationToken);
        var digs = await _queries.GetDigsAsync(run, assessmentId, cancellationToken);

        return Ok(digs);
    }

    // ------------------------------------------------------------------------
    // Helper: parse bbox=minLon,minLat,maxLon,maxLat
    // ------------------------------------------------------------------------
    // Returns true when the value is acceptable. A missing/empty bbox is valid:
    // it means "no spatial filter". The four numbers are all optional outputs
    // (null means the filter is not applied).
    //
    // InvariantCulture is important here. On a machine whose culture uses a
    // comma as the decimal separator, "101,5" could otherwise be parsed
    // incorrectly. GeoJSON and URLs always use a dot: "101.5".
    // ------------------------------------------------------------------------
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

        // No bbox supplied -> no spatial restriction.
        if (string.IsNullOrWhiteSpace(bbox))
        {
            return true;
        }

        // A GeoJSON bbox has exactly four numbers.
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

        // Reject an inverted rectangle such as minLon > maxLon. An empty
        // rectangle could be allowed, but rejecting it gives the client a
        // clearer signal that the parameters are wrong.
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
