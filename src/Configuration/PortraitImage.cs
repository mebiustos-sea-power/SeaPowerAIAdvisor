using System;
using System.IO;
using System.Linq;

namespace SeaPowerAIAdvisor;

// Read only the format and dimensions here; Unity performs the actual image decoding.
internal static class PortraitImage
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    internal const int MaxDimension = 4096;

    internal static bool SupportsPath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    internal static string Extension(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes.Length > MaxBytes) throw Invalid();
        if (bytes.Length >= 33 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes[12] == 'I' && bytes[13] == 'H' && bytes[14] == 'D' && bytes[15] == 'R')
        {
            uint Number(int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
            Dimensions(Number(16), Number(20));
            return ".png";
        }
        if (bytes[0] != 0xff || bytes[1] != 0xd8) throw Invalid();
        var position = 2;
        while (position < bytes.Length)
        {
            if (bytes[position++] != 0xff) throw Invalid();
            while (position < bytes.Length && bytes[position] == 0xff) position++;
            if (position == bytes.Length) throw Invalid();
            var marker = bytes[position++];
            if (marker == 0 || marker == 0xd8 || marker == 0xd9 || marker == 0xda) throw Invalid();
            if (marker == 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
            if (bytes.Length - position < 2) throw Invalid();
            var length = (bytes[position] << 8) | bytes[position + 1];
            if (length < 2 || length > bytes.Length - position) throw Invalid();
            // SOF markers contain dimensions; DHT, JPG and DAC markers in this range do not.
            if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc)
            {
                if (length < 8 || bytes[position + 7] == 0 || length != 8 + 3 * bytes[position + 7]) throw Invalid();
                Dimensions((uint)((bytes[position + 5] << 8) | bytes[position + 6]),
                    (uint)((bytes[position + 3] << 8) | bytes[position + 4]));
                return ".jpg";
            }
            // Skip metadata and tables by their declared lengths, including any marker-like bytes inside.
            position += length;
        }
        throw Invalid();
    }

    private static void Dimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > MaxDimension || height > MaxDimension) throw Invalid();
    }

    private static AdvisorProfileException Invalid() => new("portrait_invalid");
}
