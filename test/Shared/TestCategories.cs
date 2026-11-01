using System.Reflection;

namespace CS2OpenDev.ConfigManager.Testing;

/// <summary>
///     Category names, compiled into every test assembly as linked source. A test with no category runs
///     everywhere; the tag goes on what costs something or needs more than the process.
/// </summary>
public static class TestCategories
{
    /// <summary>Crosses a process boundary: runs another executable.</summary>
    public const string Integration = "Integration";
}

/// <summary>The repository the running test assembly was built from.</summary>
public static class Repository
{
    public static string Root { get; } =
        typeof(Repository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepositoryRoot").Value
        ?? throw new InvalidOperationException("The test assembly carries no RepositoryRoot.");
}
