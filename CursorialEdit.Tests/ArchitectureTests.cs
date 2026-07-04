using System.Reflection;

namespace CursorialEdit.Tests;

/// <summary>
/// Standing dependency-hygiene assertions (implementation-plan §2.1): Markdig is quarantined in
/// <c>CursorialEdit.Document</c>, and the promotable Dialogs project references Cursorial only.
/// </summary>
public class ArchitectureTests
{
    private static AssemblyName[] ReferencedBy(string assemblySimpleName) =>
        Assembly.Load(new AssemblyName(assemblySimpleName)).GetReferencedAssemblies();

    [Fact]
    public void OnlyDocumentProjectDeclaresMarkdig()
    {
        Assert.Contains(ReferencedBy("CursorialEdit.Document"), r => r.Name == "Markdig");
        Assert.DoesNotContain(ReferencedBy("CursorialEdit"), r => r.Name == "Markdig");
        Assert.DoesNotContain(ReferencedBy("CursorialEdit.Dialogs"), r => r.Name == "Markdig");
    }

    [Fact]
    public void DialogsProject_HasNoMarkdigOrEditorReference()
    {
        var refs = ReferencedBy("CursorialEdit.Dialogs");
        Assert.DoesNotContain(refs, r => r.Name == "Markdig");
        Assert.DoesNotContain(refs, r => r.Name == "CursorialEdit");
        Assert.DoesNotContain(refs, r => r.Name == "CursorialEdit.Document");
        // Promotability: everything it references is Cursorial or the BCL.
        Assert.All(
            refs.Where(r => !r.Name!.StartsWith("System", StringComparison.Ordinal)
                            && r.Name is not ("mscorlib" or "netstandard")),
            r => Assert.StartsWith("Cursorial.", r.Name!, StringComparison.Ordinal));
    }
}
