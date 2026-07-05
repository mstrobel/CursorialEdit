using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using CursorialEdit.Document.Model;
using CursorialEdit.Views;

namespace CursorialEdit.Tests.Views;

/// <summary>
/// M2.WP7b — the <see cref="EditorView"/> factoring (§3.2 resolution 12): the editor surface is an
/// instantiable control, so two views render over ONE shared
/// <c>DocumentBuffer</c>/<c>BlockList</c>/<c>EditController</c>/undo stack and an edit made through the
/// shared controller reflects in both (a <see cref="BlockListChange"/> fans out to each view's bridge).
/// This is the render-only substrate M7's split view instantiates — scroll-sync and caret arbitration
/// stay M7.WP2.
/// </summary>
public sealed class EditorViewTests
{
    [Fact]
    public void TwoViews_OverOneBuffer_RenderIndependently_AndEditFansOutToBoth()
    {
        using var host = UITestHost.Create(new UITestHostOptions
        {
            InitialSize = new Size(40, 8),
            Capabilities = TestSupport.CapabilityPresets.Resolve(nameof(TestCapabilities.KittyTruecolor)),
        });

        // One document model, shared by both views.
        var buffer = new DocumentBuffer("Alpha\n\nBravo");
        var controller = new EditController(buffer, host.Time);
        var producer = new MarkdigBlockProducer(controller);

        var left = new EditorView(controller, producer);
        var right = new EditorView(controller, producer);

        // A two-column split (the M7 shape); each view owns its own bridge + presenters over the one producer.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star()));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star()));
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        host.ShowRoot(grid);
        Assert.True(host.RunUntilIdle(), "the initial layout/render did not settle");

        Assert.NotSame(left.Bridge, right.Bridge); // independent presenter sets…
        Assert.Same(controller.Buffer, buffer);    // …over the one shared document model

        // Both panes render the same document (left in cols [0,20), right in [20,40)).
        Assert.Equal("Alpha", Left(host, 0));
        Assert.Equal("Alpha", Right(host, 0));
        Assert.Equal("Bravo", Left(host, 2));
        Assert.Equal("Bravo", Right(host, 2));

        // An edit through the shared controller fans out to BOTH views' bridges.
        var at = new TextPosition(0, 5);
        controller.Apply(new Edit(at, string.Empty, "!"), EditKind.Typing, new CaretState(at), new CaretState(at));
        Assert.True(host.RunUntilIdle());

        Assert.Equal("Alpha!", Left(host, 0));
        Assert.Equal("Alpha!", Right(host, 0));
    }

    private static string Left(UITestHost host, int row) => host.GetRowText(row)[..20].TrimEnd();

    private static string Right(UITestHost host, int row) => host.GetRowText(row)[20..].TrimEnd();
}
