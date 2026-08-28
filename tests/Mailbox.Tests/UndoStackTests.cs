using Mailbox.Core;

namespace Mailbox.Tests;

/// <summary>
/// The stack behind Ctrl+Z.
/// </summary>
/// <remarks>
/// The shell's own commands record their steps into this; what is held here is the behaviour a
/// reader has in their fingers — one press takes back one thing, a second press takes back the
/// one before it, redo puts them back in order, and doing something new makes the redo branch
/// unreachable rather than leaving a future that no longer applies.
/// </remarks>
public class UndoStackTests
{
    [Fact]
    public void OnePressTakesBackOneThing()
    {
        var stack = new UndoStack();
        var state = "deleted";

        stack.Push("Delete", () => state = "in the inbox", () => state = "deleted");

        Assert.True(stack.CanUndo);
        Assert.Equal("Delete", stack.NextUndo);
        Assert.Equal("Delete", stack.Undo());
        Assert.Equal("in the inbox", state);
        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void StepsComeBackInTheOrderTheyWereDone()
    {
        var stack = new UndoStack();
        var done = new List<string>();

        stack.Push("Delete", () => done.Add("undid delete"), () => done.Add("redid delete"));
        stack.Push("Move", () => done.Add("undid move"), () => done.Add("redid move"));

        stack.Undo();
        stack.Undo();

        Assert.Equal(["undid move", "undid delete"], done);

        stack.Redo();
        stack.Redo();

        Assert.Equal(["undid move", "undid delete", "redid delete", "redid move"], done);
    }

    [Fact]
    public void NothingToUndoIsNotAnError()
    {
        var stack = new UndoStack();

        Assert.Null(stack.Undo());
        Assert.Null(stack.Redo());
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void DoingSomethingNewClosesTheRedoBranch()
    {
        var stack = new UndoStack();
        stack.Push("Delete", () => { }, () => { });
        stack.Undo();

        Assert.True(stack.CanRedo);

        stack.Push("Move", () => { }, () => { });

        Assert.False(stack.CanRedo);
    }

    [Fact]
    public void AnUndoDoesNotRecordItself()
    {
        // The reverse of an action usually goes through the same command that recorded it, so a
        // step being replayed records nothing. Without this the stack never empties.
        var stack = new UndoStack();
        var pushes = 0;

        void Record() => stack.Push("Delete", Record, Record);

        stack.Push("Delete", () => { pushes++; Record(); }, () => { });
        stack.Undo();

        Assert.Equal(1, pushes);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void OnlyTheLastFewAreKept()
    {
        var stack = new UndoStack();
        for (var i = 0; i < UndoStack.Depth + 5; i++)
        {
            var which = i;
            stack.Push($"Step {which}", () => { }, () => { });
        }

        var undone = 0;
        while (stack.Undo() is not null) undone++;

        Assert.Equal(UndoStack.Depth, undone);
    }

    [Fact]
    public void ChangingTheStackSaysSo()
    {
        // The bar greys Undo when there is nothing to take back, which means it has to hear
        // about every push, undo and redo.
        var stack = new UndoStack();
        var changes = 0;
        stack.Changed += (_, _) => changes++;

        stack.Push("Delete", () => { }, () => { });
        stack.Undo();
        stack.Redo();
        stack.Clear();

        Assert.Equal(4, changes);
    }

    [Fact]
    public void ABatchIsOneStep()
    {
        // A Quick Step is one press to the reader and any number of operations underneath, each
        // of which records itself. One press has to take the whole of it back.
        var stack = new UndoStack();
        var done = new List<string>();

        using (stack.Batch("Quick Step \u201cDone\u201d"))
        {
            stack.Push("Move", () => done.Add("undid move"), () => done.Add("redid move"));
            stack.Push("Mark as Read", () => done.Add("undid read"), () => done.Add("redid read"));
        }

        Assert.Equal("Quick Step \u201cDone\u201d", stack.NextUndo);
        Assert.Equal("Quick Step \u201cDone\u201d", stack.Undo());

        // Taken back newest first, done again in the order they happened.
        Assert.Equal(["undid read", "undid move"], done);
        Assert.False(stack.CanUndo);

        stack.Redo();
        Assert.Equal(["undid read", "undid move", "redid move", "redid read"], done);
    }

    [Fact]
    public void ABatchInsideABatchJoinsIt()
    {
        // Junk batches for its own reasons — it trains the filter and moves the message — and a
        // Quick Step that junks is still one press.
        var stack = new UndoStack();
        var undone = 0;

        using (stack.Batch("Quick Step"))
        {
            stack.Push("Move", () => undone++, () => { });
            using (stack.Batch("Junk")) stack.Push("Junk", () => undone++, () => { });
        }

        Assert.Equal("Quick Step", stack.Undo());
        Assert.Equal(2, undone);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public void ABatchThatDidNothingRecordsNothing()
    {
        var stack = new UndoStack();
        var changes = 0;
        stack.Changed += (_, _) => changes++;

        using (stack.Batch("Quick Step"))
        {
        }

        Assert.False(stack.CanUndo);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ABatchOpenedWhileUndoingCollectsNothing()
    {
        // Taking a batched command back runs the same code that recorded it, which opens a batch
        // of its own. That batch has nothing to collect, and must not be left open.
        var stack = new UndoStack();

        void Undoing()
        {
            using var batch = stack.Batch("Quick Step");
            stack.Push("Move", () => { }, () => { });
        }

        stack.Push("Quick Step", Undoing, () => { });
        stack.Undo();

        Assert.False(stack.CanUndo);
        Assert.True(stack.CanRedo);
    }

    [Fact]
    public void ClearingAnEmptyStackSaysNothing()
    {
        var stack = new UndoStack();
        var changes = 0;
        stack.Changed += (_, _) => changes++;

        stack.Clear();

        Assert.Equal(0, changes);
    }

    /// <summary>
    /// An unnamed batch keeps the first step's own description.
    /// </summary>
    /// <remarks>
    /// What a selection spanning two accounts opens: the shell runs the command once per account
    /// and each records itself, so the presses have to collapse into one step — but the shell has
    /// no business naming that step, because the command already named itself. Inventing a word
    /// there would put a description on the stack that no command ever used.
    /// </remarks>
    [Fact]
    public void AnUnnamedBatchKeepsTheFirstStepsDescription()
    {
        var stack = new UndoStack();
        var back = 0;

        using (stack.Batch(string.Empty))
        {
            stack.Push("Delete", () => back++, () => { });
            stack.Push("Delete", () => back++, () => { });
            stack.Push("Delete", () => back++, () => { });
        }

        Assert.Equal(1, stack.Count);
        Assert.Equal("Delete", stack.NextUndo);

        Assert.Equal("Delete", stack.Undo());
        Assert.Equal(3, back);
        Assert.False(stack.CanUndo);
    }

    /// <summary>
    /// One press is one step however many times the command ran underneath it.
    /// </summary>
    /// <remarks>
    /// The rule the shell's per-account split has to keep. Without it, deleting a selection that
    /// spans three accounts left three steps: Ctrl+Z took back one account's share and the rest
    /// stayed deleted, which is exactly the hole the stack's own contract calls worse than no undo
    /// at all.
    /// </remarks>
    [Fact]
    public void ManyRecordingsUnderOnePressTakeOnePressToTakeBack()
    {
        var stack = new UndoStack();
        var deleted = new List<string> { "one", "two", "three" };

        using (stack.Batch(string.Empty))
        {
            foreach (var account in deleted.ToList())
            {
                var which = account;
                stack.Push("Delete", () => deleted.Remove(which), () => deleted.Add(which));
            }
        }

        Assert.Equal(1, stack.Count);

        stack.Undo();

        Assert.Empty(deleted);
        Assert.Equal(0, stack.Count);
        Assert.Equal(1, stack.RedoCount);
    }
}
