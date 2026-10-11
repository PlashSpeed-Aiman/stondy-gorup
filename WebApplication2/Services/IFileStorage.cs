namespace WebApplication2.Services;


/// <summary>
/// A port for storing and retrieving files.
/// </summary>
public interface IFileStorage
{
    public string? Write(byte[] file);
    public byte[] Read(string path);
}