using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

namespace CursorialEdit.Tests;

/// <summary>
/// M1.WP1 done-when: the wiring renders a stub root headlessly under the KittyTruecolor preset and
/// cell assertions hold — proving the project-over-package mix (app on NuGet 0.3.1, tests on
/// Cursorial project references) yields one working assembly identity end-to-end.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void StubRoot_RendersUnderKittyTruecolor()
    {
        using var host = UITestHost.Create();
        host.ShowRoot(new TextBlock { Text = "CursorialEdit" });
        host.RunUntilIdle();
        Assert.Contains("CursorialEdit", host.GetRowText(0));
    }

    [Fact]
    public void PackageAndProjectAssemblies_ShareOneCursorialUIIdentity()
    {
        // Bars types (from the NuGet package in package mode) must be assignable against the
        // project-built Cursorial.UI the test graph unifies to — the Q7-verified mix, re-asserted
        // here for every future restore.
        Assert.True(typeof(Cursorial.UI.UIElement).IsAssignableFrom(typeof(Cursorial.UI.Bars.Toolbar)));
    }
}
