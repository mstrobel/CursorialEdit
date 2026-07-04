// xUnit1031 (no blocking task ops) is deliberately disabled — UITestHost is single-thread-affine and
// the awaited dialog tasks finish on pure (non-UI) continuations, so a bounded Wait cannot deadlock.
#pragma warning disable xUnit1031

using System.Text;

using Cursorial.Input;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;

using CursorialEdit.Dialogs;

namespace CursorialEdit.Tests.Dialogs;

/// <summary>
/// M1.WP10 — the ported <see cref="MessageBox"/> and the FB-12 <see cref="ITaskDialogService"/> seam:
/// cell assertions for title/message/buttons over a stub root, the keyboard grammar (Enter default,
/// Esc cancel, Tab/arrow focus moves), the save-triad mapping through
/// <see cref="MessageBoxTaskDialogService"/>, and cancellation-driven dismissal (the
/// <c>ShowDialogAsync</c> OperationCanceledException is handled, never leaked). Runs under both
/// §5.1 capability presets (KittyTruecolor and Ansi16Legacy).
/// </summary>
public sealed class MessageBoxTests
{
    /// <summary>The §5.1 capability matrix for rendering/input suites.</summary>
    public static TheoryData<string> CapabilityPresets => new() { "KittyTruecolor", "Ansi16Legacy" };

    private static UITestHost CreateHostWithRoot(string capabilityPreset)
    {
        var host = UITestHost.Create(new UITestHostOptions
        {
            Capabilities = TestSupport.CapabilityPresets.Resolve(capabilityPreset),
        });
        host.ShowRoot(new TextBlock { Text = "editor stub" });
        Assert.True(host.RunUntilIdle());
        return host;
    }

    /// <summary>The composited screen as one string, for containment assertions.</summary>
    private static string ScreenText(UITestHost host)
    {
        var text = new StringBuilder();

        for (var row = 0; row < host.FrameBuffer.Rows; row++)
            text.AppendLine(host.GetRowText(row));

        return text.ToString();
    }

    /// <summary>
    /// Pumps the host idle, then waits out the dialog task's tail. The tail is a pure mapping
    /// continuation with no UI-thread affinity (the test thread carries no ambient
    /// UISynchronizationContext outside frames), so the bounded Wait cannot deadlock.
    /// </summary>
    private static TResult Complete<TResult>(UITestHost host, Task<TResult> task)
    {
        Assert.True(host.RunUntilIdle());
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "the dialog task did not complete");
        return task.Result;
    }

    // ── MessageBox: cells ────────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Show_RendersTitleMessageAndButtons(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = MessageBox.ShowAsync(host.Application,
                                        "Save changes to notes.md?",
                                        "CursorialEdit",
                                        MessageBoxButton.YesNoCancel);

        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("CursorialEdit", screen);             // title bar
        Assert.Contains("Save changes to notes.md?", screen); // message body
        Assert.Contains("Yes", screen);                       // buttons (access-key underscores stripped)
        Assert.Contains("No", screen);
        Assert.Contains("Cancel", screen);

        host.SendKey(Key.Escape);
        Assert.Equal(MessageBoxResult.Cancel, Complete(host, task));
    }

    // ── MessageBox: keyboard activation ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Enter_ActivatesDefaultButton(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        // No explicit default: the first button in presentation order (OK) takes Enter.
        var task = MessageBox.ShowAsync(host.Application, "Proceed?", buttons: MessageBoxButton.OkCancel);

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        Assert.Equal(MessageBoxResult.Ok, Complete(host, task));
        Assert.Empty(host.Application.WindowManager!.Windows);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Escape_ActivatesCancelButton(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = MessageBox.ShowAsync(host.Application, "Proceed?", buttons: MessageBoxButton.OkCancel);

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Escape);

        Assert.Equal(MessageBoxResult.Cancel, Complete(host, task));
        Assert.Empty(host.Application.WindowManager!.Windows);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void FocusedButtonPick_TakesEnterOverDefault(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        // Default is Yes, initial focus is No: the focused element wins Enter (bubble order beats the
        // default button's root KeyBinding) — this also proves the modal receives initial focus.
        var task = MessageBox.ShowAsync(host.Application,
                                        "Overwrite the file?",
                                        buttons: MessageBoxButton.YesNoCancel,
                                        focusedButton: MessageBoxButton.No,
                                        defaultButton: MessageBoxButton.Yes);

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        Assert.Equal(MessageBoxResult.No, Complete(host, task));
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Tab_MovesFocusToNextButton_EnterActivatesIt(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = MessageBox.ShowAsync(host.Application, "Apply?", buttons: MessageBoxButton.YesNo);

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Tab);   // Yes (default, focused) → No
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        Assert.Equal(MessageBoxResult.No, Complete(host, task));
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void ArrowKeys_MoveFocusBetweenButtons(string caps)
    {
        using var host = CreateHostWithRoot(caps);

        var task = MessageBox.ShowAsync(host.Application, "Apply?", buttons: MessageBoxButton.YesNoCancel);

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.RightArrow); // Yes → No (the button row is a directional-navigation container)
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        Assert.Equal(MessageBoxResult.No, Complete(host, task));
    }

    // ── MessageBox: dismissal by cancellation (the OCE path) ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void CanceledToken_ForceClosesBox_ReturnsNone_WithoutThrowing(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        using var cts = new CancellationTokenSource();

        var task = MessageBox.ShowAsync(host.Application,
                                        "Working…",
                                        buttons: MessageBoxButton.OkCancel,
                                        cancellationToken: cts.Token);

        Assert.True(host.RunUntilIdle());
        Assert.Single(host.Application.WindowManager!.Windows);

        cts.Cancel(); // posts the forced close; ShowDialogAsync throws OCE — MessageBox handles it

        Assert.Equal(MessageBoxResult.None, Complete(host, task));
        Assert.Empty(host.Application.WindowManager!.Windows);
    }

    // ── MessageBox: argument validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Show_WithInvalidButtonArguments_Throws()
    {
        using var host = CreateHostWithRoot("KittyTruecolor");

        await Assert.ThrowsAsync<ArgumentException>(
            () => MessageBox.ShowAsync(host.Application, "?", buttons: MessageBoxButton.None));

        await Assert.ThrowsAsync<ArgumentException>(
            () => MessageBox.ShowAsync(host.Application,
                                       "?",
                                       buttons: MessageBoxButton.OkCancel,
                                       defaultButton: MessageBoxButton.Yes)); // not among the shown set
    }

    // ── ITaskDialogService: the save-triad mapping ───────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_RendersAllThreeButtons_EnterMapsToSave(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        ITaskDialogService service = new MessageBoxTaskDialogService(host.Application);

        var task = service.ShowAsync(new TaskDialogRequest("Save changes to notes.md?")
                                     {
                                         Title = "Unsaved Changes",
                                         Content = "Unsaved edits will be lost.",
                                         Severity = TaskDialogSeverity.Warning,
                                         Buttons = TaskDialogButton.SaveTriad
                                     });

        Assert.True(host.RunUntilIdle());

        var screen = ScreenText(host);
        Assert.Contains("Unsaved Changes", screen);
        Assert.Contains("Save changes to notes.md?", screen);
        Assert.Contains("Unsaved edits will be lost.", screen);
        Assert.Contains("Save", screen);
        Assert.Contains("Don't Save", screen);
        Assert.Contains("Cancel", screen);

        host.SendKey(Key.Enter); // Save is the triad's default

        var result = Complete(host, task);
        Assert.False(result.IsDismissed);
        Assert.Equal(TaskDialogButton.Save, result.Button);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_Escape_MapsToCancelButton(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        ITaskDialogService service = new MessageBoxTaskDialogService(host.Application);

        var task = service.ShowAsync(new TaskDialogRequest("Save changes to notes.md?")
                                     {
                                         Buttons = TaskDialogButton.SaveTriad
                                     });

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Escape); // Cancel carries IsCancel in the triad

        var result = Complete(host, task);
        Assert.Equal(TaskDialogButton.Cancel, result.Button);
        Assert.False(result.IsDismissed);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void SaveTriad_TabToDontSave_MapsToDiscard(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        ITaskDialogService service = new MessageBoxTaskDialogService(host.Application);

        var task = service.ShowAsync(new TaskDialogRequest("Save changes to notes.md?")
                                     {
                                         Buttons = TaskDialogButton.SaveTriad
                                     });

        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Tab); // Save (default, focused) → Don't Save
        Assert.True(host.RunUntilIdle());
        host.SendKey(Key.Enter);

        Assert.Equal(TaskDialogButton.DontSave, Complete(host, task).Button);
    }

    // ── ITaskDialogService: dismissal + the M1 lossy-mapping contract ────────────────────────────

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Service_CanceledToken_YieldsDismissedResult_WithoutThrowing(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        using var cts = new CancellationTokenSource();
        ITaskDialogService service = new MessageBoxTaskDialogService(host.Application);

        var task = service.ShowAsync(new TaskDialogRequest("Recover the journal?")
                                     {
                                         Buttons = TaskDialogButton.SaveTriad
                                     },
                                     cts.Token);

        Assert.True(host.RunUntilIdle());
        cts.Cancel();

        var result = Complete(host, task);
        Assert.True(result.IsDismissed);
        Assert.Null(result.Button);
        Assert.Empty(host.Application.WindowManager!.Windows);
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Service_VerificationAndDetails_NotRendered_InitialCheckStateEchoed(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        ITaskDialogService service = new MessageBoxTaskDialogService(host.Application);

        var task = service.ShowAsync(new TaskDialogRequest("Restore the recovered document?")
                                     {
                                         Buttons = [TaskDialogButton.Yes, TaskDialogButton.No, TaskDialogButton.Cancel],
                                         VerificationText = "Don't ask me again",
                                         VerificationChecked = true,
                                         ExpandedInformation = "Journal: 2026-07-03 12:00"
                                     });

        Assert.True(host.RunUntilIdle());

        // The M1 implementation carries the fields but renders neither the checkbox nor the details.
        var screen = ScreenText(host);
        Assert.Contains("Restore the recovered document?", screen);
        Assert.DoesNotContain("Don't ask me again", screen);
        Assert.DoesNotContain("Journal:", screen);

        host.SendKey(Key.Enter); // no IsDefault mark: the first button (Yes) takes Enter

        var result = Complete(host, task);
        Assert.Equal(TaskDialogButton.Yes, result.Button);
        Assert.True(result.VerificationChecked); // echoed unchanged, never silently cleared
    }

    [Theory]
    [MemberData(nameof(CapabilityPresets))]
    public void Service_EmptyButtons_DefaultsToLoneOk(string caps)
    {
        using var host = CreateHostWithRoot(caps);
        ITaskDialogService service = new MessageBoxTaskDialogService(host.Application);

        var task = service.ShowAsync(new TaskDialogRequest("Export finished."));

        Assert.True(host.RunUntilIdle());
        Assert.Contains("OK", ScreenText(host));

        host.SendKey(Key.Enter);

        Assert.Equal(TaskDialogButton.Ok, Complete(host, task).Button);
    }
}
