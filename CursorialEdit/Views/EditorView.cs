using Cursorial.UI.Controls;

using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;

namespace CursorialEdit.Views;

/// <summary>
/// The instantiable editor surface (architecture §2.6 / implementation-plan §3.2 resolution 12, owned by
/// M2.WP7b): the composite of an <see cref="EditorControl"/> (which templates its own
/// <c>ScrollViewer</c> + <c>DocumentPanel</c>) and a <see cref="MarkdownViewBridge"/> feeding the
/// <see cref="Presenters.LeafBlockPresenter"/> suite. Constructing an <see cref="EditorView"/> over an
/// existing <see cref="EditController"/> + <see cref="MarkdigBlockProducer"/> wires a whole formatted,
/// reveal-on-edit markdown surface; two views over the <b>same</b> controller/producer (one
/// <c>DocumentBuffer</c>, one <c>BlockList</c>, one undo stack) render the same document independently —
/// the substrate M7's split view instantiates, not a refactor.
/// </summary>
/// <remarks>
/// Each view owns its own bridge (and thus its own presenter set), and both bridges subscribe to the one
/// shared producer's <see cref="MarkdigBlockProducer.Changed"/> feed, so a <see cref="BlockListChange"/>
/// fans out to both surfaces — an edit made through the shared controller reflects in every view. Scroll
/// synchronization and terminal-caret arbitration across views stay M7.WP2's concern; this is the
/// render-only factoring.
/// </remarks>
public sealed class EditorView : Decorator
{
    private readonly EditorControl _editor = new();

    /// <summary>Creates an unattached view (an empty editor surface); call <see cref="Attach"/> to wire a document.</summary>
    public EditorView()
    {
        Child = _editor;
    }

    /// <summary>Creates a view over <paramref name="controller"/> and <paramref name="producer"/> and attaches it.</summary>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public EditorView(EditController controller, MarkdigBlockProducer producer)
        : this()
    {
        Attach(controller, producer);
    }

    /// <summary>The templated document surface (focus owner, caret, input).</summary>
    public EditorControl Editor => _editor;

    /// <summary>The markdown bridge feeding this view's presenters (test observability; <see langword="null"/> until <see cref="Attach"/>).</summary>
    public MarkdownViewBridge? Bridge { get; private set; }

    /// <summary>
    /// Wires (or re-wires) this view over <paramref name="controller"/> and <paramref name="producer"/>:
    /// builds a fresh <see cref="MarkdownViewBridge"/> over the producer's block list and attaches it to
    /// the editor surface (replacing any previous document — the open-file path).
    /// </summary>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public void Attach(EditController controller, MarkdigBlockProducer producer)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(producer);

        Bridge = new MarkdownViewBridge(controller.Buffer, producer);
        _editor.AttachDocument(controller, Bridge);
    }
}
