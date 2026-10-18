using System;
using System.Collections;
using System.IO;
using System.Reflection;

namespace SeaPowerAIAdvisor;

internal static class ModActivation
{
    internal static bool IsEnabled(Type fileManagerType, string packagePath)
    {
        var instance = Required(fileManagerType, "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
        var manager = instance.GetValue(null) ?? throw new InvalidOperationException("Sea Power's file manager is not ready.");
        var directories = Required(fileManagerType, "Directories", BindingFlags.Instance | BindingFlags.Public).GetValue(manager) as IEnumerable
            ?? throw new InvalidOperationException("Sea Power's mod directories are unavailable.");
        foreach (var directory in directories)
        {
            if (directory == null) throw new InvalidOperationException("An empty mod directory was registered.");
            var info = Required(directory.GetType(), "DirectoryInfo", BindingFlags.Instance | BindingFlags.Public).GetValue(directory) as DirectoryInfo
                ?? throw new InvalidOperationException("A mod directory has no path.");
            if (!string.Equals(Normalize(info.FullName), Normalize(packagePath), StringComparison.OrdinalIgnoreCase)) continue;
            var selected = Required(directory.GetType(), "IsChecked", BindingFlags.Instance | BindingFlags.Public);
            if (selected.PropertyType != typeof(bool)) throw new InvalidOperationException("IsChecked is no longer a Boolean.");
            return (bool)selected.GetValue(directory)!;
        }
        throw new InvalidOperationException("The mod folder is not registered by Sea Power: " + packagePath);
    }

    private static PropertyInfo Required(Type type, string name, BindingFlags flags) =>
        type.GetProperty(name, flags) ?? throw new MissingMemberException(type.FullName, name);

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');
}
