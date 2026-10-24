using Microsoft.EntityFrameworkCore;
using PipelineProcessor.Models;
using PipelineProcessor.Persistence;
using PipelineProcessor.Persistence.Entities;

namespace PipelineProcessor.Services;

public sealed class AnomalyQueryService
{
    private readonly PipelineDbContext _dbContext;

    public AnomalyQueryService(PipelineDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> PipelineExistsAsync(string pipelineId, CancellationToken cancellationToken)
    {
        return _dbContext.Pipelines
            .AsNoTracking()
            .AnyAsync(pipeline => pipeline.Id == pipelineId, cancellationToken);
    }

    public async Task<IliRun?> ResolveRunAsync(
        string pipelineId,
        string? runId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(runId))
        {
            return await _dbContext.IliRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    run => run.Id == runId && run.PipelineId == pipelineId,
                    cancellationToken);
        }

        return await _dbContext.IliRuns
            .AsNoTracking()
            .Where(run => run.PipelineId == pipelineId && run.Status == IliRunStatus.Completed)
            .OrderByDescending(run => run.InspectionDate)
            .ThenByDescending(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<string?> ResolveAssessmentIdAsync(string runId, CancellationToken cancellationToken)
    {
        return _dbContext.Assessments
            .AsNoTracking()
            .Where(assessment => assessment.RunId == runId)
            .OrderByDescending(assessment => assessment.CreatedAt)
            .Select(assessment => (string?)assessment.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<PagedResult<AnomalyDto>> GetAnomaliesAsync(
        IliRun run,
        string? assessmentId,
        double? minErf,
        double? fromM,
        double? toM,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 1000);

        var query = ApplyFilters(BuildQuery(run.Id, assessmentId), minErf, fromM, toM);

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(row => row.Anomaly.DistanceM)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AnomalyDto>
        {
            Items = rows.Select(Map).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<List<AnomalyDto>> GetDigsAsync(
        IliRun run,
        string? assessmentId,
        CancellationToken cancellationToken)
    {
        if (assessmentId is null)
        {
            return new List<AnomalyDto>();
        }

        var rows = await BuildQuery(run.Id, assessmentId)
            .Where(row => row.Result != null && row.Result.Erf >= 1)
            .OrderByDescending(row => row.Result!.Erf)
            .ThenBy(row => row.Result!.RemainingLifeYears)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }

    public async Task<List<AnomalyDto>> GetForGeoJsonAsync(
        IliRun run,
        string? assessmentId,
        double? minLon,
        double? minLat,
        double? maxLon,
        double? maxLat,
        int limit,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(run.Id, assessmentId);

        if (minLon.HasValue)
        {
            query = query.Where(row => row.Anomaly.Lon >= minLon.Value);
        }

        if (minLat.HasValue)
        {
            query = query.Where(row => row.Anomaly.Lat >= minLat.Value);
        }

        if (maxLon.HasValue)
        {
            query = query.Where(row => row.Anomaly.Lon <= maxLon.Value);
        }

        if (maxLat.HasValue)
        {
            query = query.Where(row => row.Anomaly.Lat <= maxLat.Value);
        }

        var rows = await query
            .OrderBy(row => row.Anomaly.DistanceM)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }

    private IQueryable<AnomalyWithResult> BuildQuery(string runId, string? assessmentId)
    {
        var effectiveAssessmentId = assessmentId ?? string.Empty;

        return from anomaly in _dbContext.Anomalies.AsNoTracking()
               where anomaly.RunId == runId
               join result in _dbContext.AnomalyResults.AsNoTracking()
                       .Where(candidate => candidate.AssessmentId == effectiveAssessmentId)
                   on anomaly.Id equals result.AnomalyId into results
               from result in results.DefaultIfEmpty()
               select new AnomalyWithResult
               {
                   Anomaly = anomaly,
                   Result = result
               };
    }

    private static IQueryable<AnomalyWithResult> ApplyFilters(
        IQueryable<AnomalyWithResult> query,
        double? minErf,
        double? fromM,
        double? toM)
    {
        if (minErf.HasValue)
        {
            query = query.Where(row => row.Result != null && row.Result.Erf >= minErf.Value);
        }

        if (fromM.HasValue)
        {
            query = query.Where(row => row.Anomaly.DistanceM >= fromM.Value);
        }

        if (toM.HasValue)
        {
            query = query.Where(row => row.Anomaly.DistanceM <= toM.Value);
        }

        return query;
    }

    private static AnomalyDto Map(AnomalyWithResult row)
    {
        return new AnomalyDto
        {
            Id = row.Anomaly.Id,
            RowNumber = row.Anomaly.RowNumber,
            VendorFeatureId = row.Anomaly.VendorFeatureId,
            DistanceM = row.Anomaly.DistanceM,
            Lat = row.Anomaly.Lat,
            Lon = row.Anomaly.Lon,
            DepthPct = row.Anomaly.DepthPct,
            LengthMm = row.Anomaly.LengthMm,
            FailurePressureMpa = row.Result?.FailurePressureMpa,
            Erf = row.Result?.Erf,
            GrowthMmPerYear = row.Result?.GrowthMmPerYear,
            RemainingLifeYears = row.Result?.RemainingLifeYears
        };
    }
}

public sealed class AnomalyWithResult
{
    public Anomaly Anomaly { get; set; } = null!;

    public AnomalyResult? Result { get; set; }
}
