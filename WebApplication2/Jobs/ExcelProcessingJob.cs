using System.Text.Json;
using ClosedXML.Excel;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Converters;
using WebApplication2.DbContexts;
using WebApplication2.Services;
using Geometry = WebApplication2.Entities.Geometry;

namespace WebApplication2;

public class ExcelProcessingJob
{
    private readonly IFileStorage _fileStorage;
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<ExcelProcessingJob> _logger;

    public ExcelProcessingJob(
        IFileStorage fileStorage,
        ApplicationDbContext dbContext,
        ILogger<ExcelProcessingJob> logger)
    {
        _fileStorage = fileStorage;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Process(string filePath)
    {
        _logger.LogInformation("Starting Excel processing for file: {FilePath}", filePath);

        var fileBytes = _fileStorage.Read(filePath);

        _logger.LogInformation(
            "Loaded Excel file from storage. File: {FilePath}, Size: {FileSize} bytes",
            filePath,
            fileBytes.Length);

        var coordinates = ExtractCoordinates(fileBytes);

        if (coordinates.Count == 0)
        {
            _logger.LogWarning("No latitude/longitude values found in file: {FilePath}", filePath);
            return;
        }

        var geoJson = BuildGeoJson(coordinates);

        var geometry = new Geometry
        {
            Id = filePath,
            Data = geoJson
        };
        // simulate very long processing time
        await Task.Delay(60000);

        await _dbContext.Geometries.AddAsync(geometry);
        await _dbContext.SaveChangesAsync();
        _logger.LogInformation(
            "Stored GeoJSON with {PointCount} points (id: {GeometryId}) for file: {FilePath}",
            coordinates.Count,
            geometry.Id,
            filePath);
    }

    private static List<(double Latitude, double Longitude)> ExtractCoordinates(byte[] fileBytes)
    {
        var coordinates = new List<(double Latitude, double Longitude)>();

        using var stream = new MemoryStream(fileBytes);
        using var workbook = new XLWorkbook(stream);

        var worksheet = workbook.Worksheets.First();
        var usedRange = worksheet.RangeUsed();

        if (usedRange is null)
        {
            return coordinates;
        }

        var headerRow = usedRange.FirstRow();
        var latitudeColumn = 0;
        var longitudeColumn = 0;

        foreach (var cell in headerRow.CellsUsed())
        {
            var header = cell.GetString().Trim().ToLowerInvariant();

            if (header.Contains("lat"))
            {
                latitudeColumn = cell.Address.ColumnNumber;
            }
            else if (header.Contains("lon") || header.Contains("lng"))
            {
                longitudeColumn = cell.Address.ColumnNumber;
            }
        }

        if (latitudeColumn == 0 || longitudeColumn == 0)
        {
            return coordinates;
        }

        var lastRowNumber = usedRange.LastRow().RowNumber();

        for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRowNumber; rowNumber++)
        {
            var row = worksheet.Row(rowNumber);

            if (row.Cell(latitudeColumn).TryGetValue<double>(out var latitude) &&
                row.Cell(longitudeColumn).TryGetValue<double>(out var longitude))
            {
                coordinates.Add((latitude, longitude));
            }
        }

        return coordinates;
    }

    private static string BuildGeoJson(List<(double Latitude, double Longitude)> coordinates)
    {
        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        var featureCollection = new FeatureCollection();

        foreach (var (latitude, longitude) in coordinates)
        {
            var point = geometryFactory.CreatePoint(new Coordinate(longitude, latitude));

            var attributes = new AttributesTable
            {
                { "latitude", latitude },
                { "longitude", longitude }
            };

            featureCollection.Add(new Feature(point, attributes));
        }

        var options = new JsonSerializerOptions();
        options.Converters.Add(new GeoJsonConverterFactory());

        return JsonSerializer.Serialize(featureCollection, options);
    }
}
