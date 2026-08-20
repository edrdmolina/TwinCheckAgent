using ImageMagick;
using TwinCheck.Agent.Core;

namespace TwinCheck.Agent.Imaging;

public sealed class MagickScanImageConverter : IScanImageConverter
{
    public void ConvertBmpToTiff(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var source = new MagickImage(sourcePath);
        var sourceWidth = source.Width;
        var sourceHeight = source.Height;
        var sourceDensityX = source.Density.X;
        var sourceDensityY = source.Density.Y;
        var sourceDensityUnits = source.Density.Units;
        var sourceColorProfile = source.GetColorProfile()?.ToByteArray();

        source.Format = MagickFormat.Tiff;
        source.SetCompression(CompressionMethod.NoCompression);
        source.Write(destinationPath);

        using var converted = new MagickImage(destinationPath);
        if (converted.Format is not MagickFormat.Tif and not MagickFormat.Tiff)
        {
            throw new InvalidDataException($"Converted file is not TIFF: '{destinationPath}'.");
        }

        if (converted.Compression != CompressionMethod.NoCompression)
        {
            throw new InvalidDataException($"Converted TIFF is compressed: '{destinationPath}'.");
        }

        if (converted.Width != sourceWidth || converted.Height != sourceHeight)
        {
            throw new InvalidDataException($"Converted TIFF dimensions do not match the source BMP: '{sourcePath}'.");
        }

        if (converted.Density.Units != sourceDensityUnits
            || Math.Abs(converted.Density.X - sourceDensityX) > 0.01
            || Math.Abs(converted.Density.Y - sourceDensityY) > 0.01)
        {
            throw new InvalidDataException($"Converted TIFF resolution does not match the source BMP: '{sourcePath}'.");
        }

        if (sourceColorProfile is not null)
        {
            var convertedColorProfile = converted.GetColorProfile()?.ToByteArray();
            if (convertedColorProfile is null || !sourceColorProfile.SequenceEqual(convertedColorProfile))
            {
                throw new InvalidDataException($"Converted TIFF did not preserve the source BMP color profile: '{sourcePath}'.");
            }
        }

        var distortion = source.Compare(converted, ErrorMetric.Absolute);
        if (distortion != 0)
        {
            throw new InvalidDataException($"Converted TIFF pixels do not exactly match the source BMP: '{sourcePath}'.");
        }
    }
}
