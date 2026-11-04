using System;
using System.IO;
using System.Text;

namespace SeaPowerAIAdvisor.Shared;

// Shared file operations; each settings owner translates I/O failures into its own error codes.
internal static class ConfigFile
{
    internal static string Decode(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    internal static byte[]? Backup(string path)
    {
        if (!File.Exists(path)) return null;
        return File.ReadAllBytes(path);
    }
    internal static void Restore(string path, byte[]? before)
    {
        if (before != null) Write(path, before);
        else File.Delete(path);
    }
    // Keep the exact previous document. A failed backup must never replace the settings file.
    internal static void WriteMigration(string path, byte[] before, byte[] after)
    {
        Write(path + ".bak", before);
        Write(path, after);
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
