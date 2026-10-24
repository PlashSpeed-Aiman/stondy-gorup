using System.Globalization;
using ClosedXML.Excel;

namespace PipelineProcessor.Services;

public sealed record ParsedAnomaly(
    int RowNumber,
    string VendorFeatureId,
    double DistanceM,
    double Lat,
    double Lon,
    double DepthPct,
    double LengthMm);

public sealed record IliRowError(int RowNumber, string Message);

public sealed class IliParseResult
{
    public List<ParsedAnomaly> Anomalies { get; } = new();

    public List<IliRowError> Errors { get; } = new();

    public string? FatalError { get; set; }
}

/// <summary>
/// Reads the vendor spreadsheet into rows. Pure: no database, no HTTP.
/// </summary>
public static class IliRowParser
{
    private const string FeatureIdColumn = "feature_id";
    private const string DistanceColumn = "distance_m";
    private const string LatColumn = "lat";
    private const string LonColumn = "lon";
    private const string DepthColumn = "depth_pct";
    private const string LengthColumn = "length_mm";

    private static readonly string[] RequiredColumns =
    {
        FeatureIdColumn, DistanceColumn, LatColumn, LonColumn, DepthColumn, LengthColumn
    };

    public static IliParseResult Parse(byte[] fileBytes)
    {
        var result = new IliParseResult();

        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        var worksheet = workbook.Worksheets.First();
        var usedRange = worksheet.RangeUsed();

        if (usedRange is null)
        {
            result.FatalError = "The spreadsheet is empty.";
            return result;
        }

        var headerRow = usedRange.FirstRow();
        var columns = ReadHeaders(headerRow);

        var missing = RequiredColumns.Where(column => !columns.ContainsKey(column)).ToArray();
        if (missing.Length > 0)
        {
            result.FatalError = $"Missing required column(s): {string.Join(", ", missing)}.";
            return result;
        }

        var lastRowNumber = usedRange.LastRow().RowNumber();

        for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRowNumber; rowNumber++)
        {
            var row = worksheet.Row(rowNumber);

            if (RequiredColumns.All(column => row.Cell(columns[column]).IsEmpty()))
            {
                continue;
            }

            var parsed = ParseRow(rowNumber, row, columns, out var error);
            if (parsed is null)
            {
                result.Errors.Add(new IliRowError(rowNumber, error!));
                continue;
            }

            result.Anomalies.Add(parsed);
        }

        return result;
    }

    private static Dictionary<string, int> ReadHeaders(IXLRangeRow headerRow)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in headerRow.CellsUsed())
        {
            var header = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(header))
            {
                columns[header] = cell.Address.ColumnNumber;
            }
        }

        return columns;
    }

    private static ParsedAnomaly? ParseRow(
        int rowNumber,
        IXLRow row,
        IReadOnlyDictionary<string, int> columns,
        out string? error)
    {
        error = null;

        var featureId = row.Cell(columns[FeatureIdColumn]).GetString().Trim();
        if (string.IsNullOrWhiteSpace(featureId))
        {
            error = "feature_id is empty.";
            return null;
        }

        if (!TryReadNumber(row.Cell(columns[DistanceColumn]), out var distanceM))
        {
            error = "distance_m is not a number.";
            return null;
        }

        if (!TryReadNumber(row.Cell(columns[LatColumn]), out var lat))
        {
            error = "lat is not a number.";
            return null;
        }

        if (!TryReadNumber(row.Cell(columns[LonColumn]), out var lon))
        {
            error = "lon is not a number.";
            return null;
        }

        if (!TryReadNumber(row.Cell(columns[DepthColumn]), out var depthPct))
        {
            error = "depth_pct is not a number.";
            return null;
        }

        if (!TryReadNumber(row.Cell(columns[LengthColumn]), out var lengthMm))
        {
            error = "length_mm is not a number.";
            return null;
        }

        if (distanceM < 0)
        {
            error = "distance_m must be zero or greater.";
            return null;
        }

        if (lat is < -90 or > 90)
        {
            error = "lat must be between -90 and 90.";
            return null;
        }

        if (lon is < -180 or > 180)
        {
            error = "lon must be between -180 and 180.";
            return null;
        }

        if (depthPct is < 0 or > 100)
        {
            error = "depth_pct must be between 0 and 100.";
            return null;
        }

        if (lengthMm <= 0)
        {
            error = "length_mm must be greater than zero.";
            return null;
        }

        return new ParsedAnomaly(rowNumber, featureId, distanceM, lat, lon, depthPct, lengthMm);
    }

    private static bool TryReadNumber(IXLCell cell, out double value)
    {
        if (cell.TryGetValue(out value))
        {
            return true;
        }

        var text = cell.GetString().Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
