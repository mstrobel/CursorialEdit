using CursorialEdit.Document.Buffer;
using CursorialEdit.Document.Editing;
using Microsoft.Extensions.Time.Testing;

namespace CursorialEdit.Tests.Document;

/// <summary>
/// M1.WP5 fuzz gate: seeded random edit scripts (BufferFuzzTests' corpus style) driven through
/// <see cref="EditController"/> with fake time, mixing coalescing typing runs, Backspace/Delete
/// runs, Newline/Structural/Paste units, caret-move and idle seals, and mid-script undo/redo.
/// A group mirror — built by observing <see cref="EditController.UndoDepth"/>, never by
/// re-deriving the coalescing rules — records each group's before/after document and caret;
/// the suite asserts every mid-script undo/redo lands on its snapshot, that mandatory seals
/// (idle, caret move, non-Typing kinds, undo/redo, explicit) always open a new group, and
/// finally that <b>undo-to-bottom reproduces the original document + caret stepwise, and
/// redo-to-top the final state</b>.
/// </summary>
public class UndoFuzzTests
{
    /// <summary>Ops per seed; scaled up for nightly runs via the environment (plan §2.1).</summary>
    private static int OpsPerSeed =>
        int.TryParse(Environment.GetEnvironmentVariable("CURSORIALEDIT_FUZZ_OPS"), out int ops) && ops > 0
            ? ops
            : 1200;

    private static readonly TimeSpan IdleTimeout = EditController.DefaultIdleSealTimeout;

    private static readonly string[] Tokens =
    [
        "alpha", "beta gamma", "0123", " ", "_",
        "\n", "\r\n", "\r",
        "漢字テスト", "한글",
        "😀", "🎉🎉", "👨‍👩‍👧‍👦", "🏳️‍🌈",
        "e\u0301", "n\u0303o\u0308", "\u200D", "a\uD83D", // combining marks, a bare ZWJ, a lone high surrogate
    ];

    /// <summary>One recorded group as observed from outside: the states undo/redo must land on.</summary>
    private sealed class GroupSnapshot
    {
        public required string TextBefore;
        public required CaretState CaretBefore;
        public required string TextAfter;
        public required CaretState CaretAfter;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(20260703)]
    public void EditScript_UndoToBottomReproducesOriginal_RedoToTopReproducesFinal(int seed)
    {
        var rng = new Random(seed);
        var time = new FakeTimeProvider();

        string text = GenerateText(rng, rng.Next(5, 40));
        string original = text;
        var buffer = new DocumentBuffer(text);
        var controller = new EditController(buffer, time, undoDepthLimit: 1_000_000);

        long spliceCount = 0;
        controller.Changed += _ => spliceCount++;

        var undoMirror = new List<GroupSnapshot>();
        var redoMirror = new List<GroupSnapshot>();

        int caretOffset = 0;
        bool mustSeal = false;                    // the next recorded edit MUST start a new group
        var idleSinceLastRecord = TimeSpan.Zero;  // fake time elapsed since the last recorded edit
        CaretState lastAppliedAfter = default;

        int ops = OpsPerSeed;
        for (int op = 0; op < ops; op++)
        {
            int roll = rng.Next(100);

            if (roll < 55)
            {
                // ---- Typing: insert at the caret, Backspace, or forward Delete. ----
                int sub = rng.Next(100);
                int startOffset, removedLength;
                string inserted;

                if (sub < 60 || text.Length == 0)
                {
                    startOffset = Normalize(buffer, caretOffset);
                    removedLength = 0;
                    inserted = Tokens[rng.Next(Tokens.Length)];
                }
                else if (sub < 85)
                {
                    // Backspace: remove up to 3 units ending at the caret.
                    int end = Normalize(buffer, caretOffset);
                    startOffset = Normalize(buffer, Math.Max(0, end - rng.Next(1, 4)));
                    removedLength = end - startOffset;
                    inserted = "";
                    if (removedLength == 0)
                        continue;
                }
                else
                {
                    // Forward delete: remove up to 3 units after the caret.
                    startOffset = Normalize(buffer, caretOffset);
                    removedLength = Math.Min(rng.Next(1, 4), text.Length - startOffset);
                    inserted = "";
                    if (removedLength == 0)
                        continue;
                }

                caretOffset = ApplyAndMirror(
                    buffer, controller, EditKind.Typing, startOffset, removedLength, inserted,
                    ref text, ref mustSeal, ref lastAppliedAfter, undoMirror, redoMirror, rng);
                idleSinceLastRecord = TimeSpan.Zero;
            }
            else if (roll < 68)
            {
                // ---- A non-Typing unit at a random spot: Newline / Structural / Paste. ----
                var kind = rng.Next(3) switch
                {
                    0 => EditKind.Newline,
                    1 => EditKind.Structural,
                    _ => EditKind.Paste,
                };

                int startOffset = Normalize(buffer, rng.Next(text.Length + 1));
                int removedLength = kind == EditKind.Structural
                    ? Math.Min(rng.Next(0, 30), text.Length - startOffset)
                    : 0;
                string inserted = kind switch
                {
                    EditKind.Newline => rng.Next(2) == 0 ? "\n" : "\r\n",
                    _ => GenerateText(rng, rng.Next(1, 6)),
                };

                caretOffset = ApplyAndMirror(
                    buffer, controller, kind, startOffset, removedLength, inserted,
                    ref text, ref mustSeal, ref lastAppliedAfter, undoMirror, redoMirror, rng);
                idleSinceLastRecord = TimeSpan.Zero;
                mustSeal = true; // nothing coalesces after an atomic unit
            }
            else if (roll < 75)
            {
                // ---- Independent caret move. ----
                caretOffset = Normalize(buffer, rng.Next(text.Length + 1));
                var moved = new CaretState(buffer.GetPosition(caretOffset));
                controller.NotifyCaretMoved(moved);
                if (moved != lastAppliedAfter)
                    mustSeal = true; // an echo of the last edit's landing may legally keep the run open
            }
            else if (roll < 83)
            {
                // ---- Idle time. ----
                var delta = TimeSpan.FromMilliseconds(rng.Next(0, 1000));
                time.Advance(delta);
                idleSinceLastRecord += delta;
                if (idleSinceLastRecord >= IdleTimeout)
                    mustSeal = true;
            }
            else if (roll < 88)
            {
                controller.SealGroup();
                mustSeal = true;
            }
            else if (roll < 95)
            {
                // ---- Mid-script undo. ----
                if (undoMirror.Count == 0)
                {
                    Assert.False(controller.CanUndo);
                    continue;
                }

                var snapshot = undoMirror[^1];
                undoMirror.RemoveAt(undoMirror.Count - 1);
                var caret = controller.Undo();

                Assert.Equal(snapshot.CaretBefore, caret);
                text = snapshot.TextBefore;
                Assert.Equal(text, buffer.GetText());

                redoMirror.Add(snapshot);
                caretOffset = Normalize(buffer, buffer.GetOffset(snapshot.CaretBefore.Position));
                mustSeal = true;
            }
            else
            {
                // ---- Mid-script redo. ----
                if (redoMirror.Count == 0)
                {
                    Assert.False(controller.CanRedo);
                    continue;
                }

                var snapshot = redoMirror[^1];
                redoMirror.RemoveAt(redoMirror.Count - 1);
                var caret = controller.Redo();

                Assert.Equal(snapshot.CaretAfter, caret);
                text = snapshot.TextAfter;
                Assert.Equal(text, buffer.GetText());

                undoMirror.Add(snapshot);
                caretOffset = Normalize(buffer, buffer.GetOffset(snapshot.CaretAfter.Position));
                mustSeal = true;
            }

            Assert.Equal(undoMirror.Count, controller.UndoDepth);
            Assert.Equal(redoMirror.Count, controller.RedoDepth);
        }

        // ---- Undo to bottom: every step lands on its group's before-state. ----
        string finalText = text;
        for (int i = undoMirror.Count - 1; i >= 0; i--)
        {
            var caret = controller.Undo();
            Assert.Equal(undoMirror[i].CaretBefore, caret);
            Assert.Equal(undoMirror[i].TextBefore, buffer.GetText());
        }

        Assert.False(controller.CanUndo);
        Assert.Null(controller.Undo());
        Assert.Equal(original, buffer.GetText()); // the original document, byte-exact

        // ---- Redo to top: every step lands on its group's after-state. ----
        for (int i = 0; i < undoMirror.Count; i++)
        {
            var caret = controller.Redo();
            Assert.Equal(undoMirror[i].CaretAfter, caret);
            Assert.Equal(undoMirror[i].TextAfter, buffer.GetText());
        }

        Assert.False(controller.CanRedo);
        Assert.Null(controller.Redo());
        Assert.Equal(finalText, buffer.GetText()); // the final document, byte-exact

        Assert.True(spliceCount >= ops / 4, "Changed must have fired for every splice — the script should have produced plenty.");
    }

    // ---- One recorded application + mirror bookkeeping -----------------------------------------

    /// <summary>
    /// Applies one recorded edit and maintains the group mirror by <b>observing</b> whether the
    /// controller opened a new group (depth grew) or coalesced (depth unchanged — the top
    /// snapshot's after-state advances). When a mandatory seal is pending, or the kind is
    /// atomic, a new group is asserted. Returns the new caret offset (the splice end).
    /// </summary>
    private static int ApplyAndMirror(
        DocumentBuffer buffer, EditController controller, EditKind kind,
        int startOffset, int removedLength, string inserted,
        ref string text, ref bool mustSeal, ref CaretState lastAppliedAfter,
        List<GroupSnapshot> undoMirror, List<GroupSnapshot> redoMirror, Random rng)
    {
        string removed = text.Substring(startOffset, removedLength);
        var start = buffer.GetPosition(startOffset);
        Assert.Equal(startOffset, buffer.GetOffset(start)); // normalized offsets always address a real position

        var before = new CaretState(
            buffer.GetPosition(startOffset + removedLength),
            removedLength > 0 && rng.Next(2) == 0 ? start : null); // sometimes the removal is a "selection"

        string preText = text;
        string newText = string.Concat(text.AsSpan(0, startOffset), inserted, text.AsSpan(startOffset + removedLength));
        var after = new CaretState(OraclePosition(newText, startOffset + inserted.Length));

        int depthBefore = controller.UndoDepth;
        var result = controller.Apply(new Edit(start, removed, inserted), kind, before, after);

        // Receipt + document oracle.
        Assert.Equal(startOffset, result.StartOffset);
        Assert.Equal(removed, result.RemovedText);
        Assert.Equal(after.Position, result.End);
        text = newText;
        Assert.Equal(text, buffer.GetText());

        redoMirror.Clear(); // any recorded edit invalidates the redo branch

        if (controller.UndoDepth == depthBefore + 1)
        {
            undoMirror.Add(new GroupSnapshot
            {
                TextBefore = preText, CaretBefore = before,
                TextAfter = text, CaretAfter = after,
            });
        }
        else
        {
            // Coalesced into the open group: legal only for Typing with no seal pending.
            Assert.Equal(depthBefore, controller.UndoDepth);
            Assert.False(mustSeal || kind != EditKind.Typing,
                "A mandatory seal was pending (or the kind is atomic); the controller must not have coalesced this edit.");
            Assert.True(undoMirror.Count > 0);
            undoMirror[^1].TextAfter = text;
            undoMirror[^1].CaretAfter = after;
        }

        mustSeal = false;
        lastAppliedAfter = after;
        return buffer.GetOffset(result.End);
    }

    // ---- Corpus helpers --------------------------------------------------------------------------

    private static string GenerateText(Random rng, int tokenCount)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < tokenCount; i++)
            builder.Append(Tokens[rng.Next(Tokens.Length)]);
        return builder.ToString();
    }

    /// <summary>Snaps an offset out of CRLF-terminator interiors so it addresses a real position.</summary>
    private static int Normalize(DocumentBuffer buffer, int offset) =>
        buffer.GetOffset(buffer.GetPosition(Math.Min(offset, buffer.Length)));

    /// <summary>
    /// Offset → position over a raw string, with the documented snap: '\n' terminates a line, a
    /// directly preceding '\r' folds into the terminator, and an offset inside a CRLF terminator
    /// clamps to that line's end-of-text — the naive mirror of <see cref="DocumentBuffer.GetPosition"/>.
    /// </summary>
    private static TextPosition OraclePosition(string text, int offset)
    {
        int line = 0, start = 0;

        while (true)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0)
                return new TextPosition(line, offset - start); // last (unterminated) line

            if (offset <= nl)
            {
                int textEnd = nl > start && text[nl - 1] == '\r' ? nl - 1 : nl;
                return new TextPosition(line, Math.Min(offset, textEnd) - start);
            }

            start = nl + 1;
            line++;
        }
    }
}
