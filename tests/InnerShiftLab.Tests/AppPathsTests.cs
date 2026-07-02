using InnerShiftLab.Core;
using Xunit;

namespace InnerShiftLab.Tests;

public class AppPathsTests
{
    [Fact]
    public void ResolveRoot_FindsDirectoryContainingMarkerFile()
    {
        var tmp = Directory.CreateTempSubdirectory("iris_root_");
        try
        {
            File.WriteAllText(Path.Combine(tmp.FullName, "hooks.json"), "{}");
            var sub = Directory.CreateDirectory(Path.Combine(tmp.FullName, "src"));

            var root = AppPaths.ResolveRoot(sub.FullName);

            Assert.Equal(Path.GetFullPath(tmp.FullName), root);
        }
        finally { tmp.Delete(true); }
    }

    [Fact]
    public void ResolveConnectionString_AnchorsRelativePathToRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "iris_app_root");
        var result = AppPaths.ResolveConnectionString("Data Source=data/iris.db", root);

        Assert.Contains(Path.Combine(root, "data", "iris.db"), result);
        Assert.StartsWith("Data Source=", result);
    }

    [Fact]
    public void ResolveConnectionString_LeavesAbsolutePathUntouched()
    {
        var abs = OperatingSystem.IsWindows() ? @"C:\db\iris.db" : "/var/lib/iris.db";
        var conn = $"Data Source={abs}";
        Assert.Equal(conn, AppPaths.ResolveConnectionString(conn, "/some/root"));
    }

    [Fact]
    public void ResolveConnectionString_LeavesInMemoryUntouched()
    {
        var conn = "Data Source=:memory:";
        Assert.Equal(conn, AppPaths.ResolveConnectionString(conn, "/some/root"));
    }
}
