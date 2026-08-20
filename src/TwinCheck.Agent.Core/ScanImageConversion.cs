namespace TwinCheck.Agent.Core;

public interface IScanImageConverter
{
    void ConvertBmpToTiff(string sourcePath, string destinationPath);
}

public static class ScanFileConversions
{
    public const string BmpToTiff = "bmp-to-tiff";
}

internal sealed class UnavailableScanImageConverter : IScanImageConverter
{
    public static readonly UnavailableScanImageConverter Instance = new();

    public void ConvertBmpToTiff(string sourcePath, string destinationPath) =>
        throw new InvalidOperationException("BMP-to-TIFF conversion is enabled, but no image converter is configured.");
}
