using System;
using System.IO;

namespace SeaPowerAIAdvisor.Shared;

// Shared file operations; each settings owner translates I/O failures into its own error codes.
internal static class ConfigFile
{
    internal static byte[]? Backup(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Settings file is too large.");
        return File.ReadAllBytes(path);
    }
    internal static void Restore(string path, byte[]? before)
    {
        if (before != null) Write(path, before);
        else File.Delete(path);
    }
    internal static void Write(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes, 0, bytes.Length);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
