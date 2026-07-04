using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Tests.Blocks;

namespace CursorialEdit.Tests.Incremental;

/// <summary>
/// M2 wave-2 review fixes for the document-global definition machinery:
/// (1) <see cref="BlockListChange.Invalidated"/> is a subset of Reused (the documented partition
/// invariant), so a paste that both changes a definition and adds/changes a referencing block never
/// double-buckets an id; (2) the definition-signature is FIRST-wins (Markdig/CommonMark), so editing
/// the effective (first) of a duplicated label is detected and its referencing blocks are invalidated.
/// </summary>
public sealed class DefinitionInvalidationTests
{
    private static BlockId[] Invalidated(BlockListChange change) => [.. change.Invalidated];

    [Fact]
    public void Invalidated_IsAlwaysASubsetOfReused()
    {
        // A paragraph references [foo]; the link-reference definition already exists.
        var h = BlockHarness.Create("see [foo] here\n\nmid\n\n[foo]: https://a.example\n\ntail");

        // Change the definition target — the referencing paragraph's source is unchanged (Reused), so
        // it is reported Invalidated. The invalidated set must be drawn only from Reused.
        var defLine = h.Blocks.Count; // find the [foo]: line index
        var change = h.Apply(new TextPosition(4, 7), "https://a.example", "https://b.example", EditKind.Typing);

        var reused = new HashSet<BlockId>(change.Reused);
        Assert.All(Invalidated(change), id => Assert.Contains(id, reused)); // ⊆ Reused
        Assert.DoesNotContain(Invalidated(change), change.Changed.Contains);
        Assert.DoesNotContain(Invalidated(change), change.Added.Contains);
        _ = defLine;
    }

    [Fact]
    public void EditingTheFirstOfADuplicatedDefinition_IsDetected_AndInvalidatesReferences()
    {
        // Two definitions of [foo]; Markdig/CommonMark render the FIRST (/url1). A paragraph uses it.
        var h = BlockHarness.Create("use [foo]\n\n[foo]: /url1\n\n[foo]: /url2\n\ntail");
        var paraId = h.Blocks[0].Id;

        // Edit the EFFECTIVE (first) definition. A last-wins signature would miss this (it tracked
        // /url2); first-wins detects the change and invalidates the referencing paragraph.
        var change = h.Apply(new TextPosition(2, 7), "/url1", "/urlX", EditKind.Typing);

        Assert.Contains(paraId, Invalidated(change)); // the [foo] reference was flagged for re-realizing
    }

    [Fact]
    public void EditingTheShadowedSecondDefinition_DoesNotInvalidate()
    {
        var h = BlockHarness.Create("use [foo]\n\n[foo]: /url1\n\n[foo]: /url2\n\ntail");

        // The second definition is shadowed (first-wins), so changing it renders no differently — no
        // referencing block needs re-realizing.
        var change = h.Apply(new TextPosition(4, 7), "/url2", "/urlZ", EditKind.Typing);

        Assert.Empty(change.Invalidated);
    }
}
