namespace CursorialEdit.Tests.Conformance;

/// <summary>
/// Locates the repository root from the running test assembly so the conformance generator can write
/// the checked-in <c>docs/conformance.md</c> artifact regardless of the working directory. Walks up
/// from the test binary until it finds the solution file that marks the root.
/// </summary>
public static class RepoLocator
{
    private const string RootMarker = "CursorialEdit.slnx";

    /// <summary>The repository root directory (the folder containing <c>CursorialEdit.slnx</c>).</summary>
    /// <exception cref="InvalidOperationException">The marker was not found in any ancestor.</exception>
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, RootMarker)))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate '{RootMarker}' walking up from '{AppContext.BaseDirectory}'.");
        }
    }

    /// <summary>The absolute path of the generated conformance document (<c>docs/conformance.md</c>).</summary>
    public static string ConformanceDocPath => Path.Combine(RepoRoot, "docs", "conformance.md");
}
