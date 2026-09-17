using System;
using System.IO;

namespace KillerScan.Services;

internal static class SaveFileNamePolicy
{
    internal static string ApplyExtension(string path, string? extension, bool addExtension, bool requireExtension)
    {
        if (!addExtension || string.IsNullOrWhiteSpace(extension)) return path;

        string normalized = extension!.StartsWith(".") ? extension : "." + extension;
        string current = Path.GetExtension(path);
        if (current.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return path;
        if (current.Length == 0 || requireExtension) return path + normalized;
        return path;
    }
}
