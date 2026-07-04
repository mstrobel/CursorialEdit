namespace CursorialEdit.Document.Parsing;

/// <summary>
/// Pins a real Markdig usage into this assembly so the compiled metadata carries the reference —
/// <c>ArchitectureTests.OnlyDocumentProjectDeclaresMarkdig</c> asserts against it. The M2 pipeline
/// factory replaces this as the assembly's substantive Markdig consumer.
/// </summary>
internal static class MarkdigPin
{
    internal static string MarkdigAssemblyVersion =>
        typeof(Markdig.MarkdownPipeline).Assembly.GetName().Version?.ToString() ?? "unknown";
}
