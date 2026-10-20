using System;
using System.IO;

internal static class TestRepository
{
    internal static DirectoryInfo Find()
    {
        string? configured = Environment.GetEnvironmentVariable("KILLERSCAN_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var root = new DirectoryInfo(configured);
            if (File.Exists(Path.Combine(root.FullName, "KillerScan.csproj"))) return root;
            throw new DirectoryNotFoundException("KILLERSCAN_TEST_ROOT must contain KillerScan.csproj.");
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj")))
            directory = directory.Parent;
        return directory ?? throw new DirectoryNotFoundException("KillerScan repository root not found. Set KILLERSCAN_TEST_ROOT for isolated build output.");
    }
}
