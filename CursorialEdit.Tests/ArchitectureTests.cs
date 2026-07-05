using System.Reflection;

namespace CursorialEdit.Tests;

/// <summary>
/// Standing dependency-hygiene assertions (implementation-plan §2.1): Markdig is quarantined in
/// <c>CursorialEdit.Document</c>. (The dialog suite was promoted to the framework's
/// <c>Cursorial.UI.Dialogs</c>; the editor no longer ships a Dialogs project to police here.)
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
    }
}
