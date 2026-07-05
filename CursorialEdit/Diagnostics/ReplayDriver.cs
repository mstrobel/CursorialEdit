using Cursorial.Input.Events;
using Cursorial.UI;

namespace CursorialEdit.Diagnostics;

/// <summary>
/// Drives recorded <see cref="InputEvent"/>s back into a running application through the <b>real</b>
/// input system, so a replay reproduces a captured session deterministically. Every event flows
/// through <c>InputDispatcher.ProcessEvent</c> — the exact method the frame loop uses to dispatch
/// live input — which routes it to the focused element just as it was routed during capture. A
/// resize is the one exception: it is not an <c>InputDispatcher</c> concern, so it is applied via
/// <see cref="UIApplication.NotifyResized"/>.
/// </summary>
/// <remarks>
/// <b>Threading.</b> Both <c>ProcessEvent</c> and <see cref="UIApplication.NotifyResized"/> are
/// UI-thread-only. Call <see cref="Inject"/> on the application's UI thread (in tests the calling
/// thread is the UI thread; in the running app, from a dispatcher/`Started` continuation) and step a
/// frame between events so the UI settles.
/// </remarks>
public static class ReplayDriver
{
    /// <summary>
    /// Injects one recorded event. Key/mouse/paste events go through
    /// <c>InputDispatcher.ProcessEvent</c> (which re-synthesizes text input from printable keys, just
    /// as during capture); a <see cref="ResizeEvent"/> is applied through
    /// <see cref="UIApplication.NotifyResized"/>.
    /// </summary>
    public static void Inject(UIApplication application, InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(inputEvent);

        switch (inputEvent)
        {
            case ResizeEvent resize:
                application.NotifyResized(resize.Columns, resize.Rows);
                break;

            default:
                application.InputDispatcher.ProcessEvent(inputEvent);
                break;
        }
    }
}
