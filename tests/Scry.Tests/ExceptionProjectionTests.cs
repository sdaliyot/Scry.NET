using Scry.Contracts;

namespace Scry.Tests;

/// <summary>
/// Covers <see cref="ExceptionDetail.FromException"/>, the wire projection every error envelope
/// goes through.
///
/// <para>
/// Two defects motivated this: it followed <c>Exception.InnerException</c> with no depth limit,
/// so an unusually deep chain would not be truncated - it would overrun
/// <c>ScryJson.MaximumJsonDepth</c> or the frame's byte cap and fail to serialize, which means
/// the error path could fail to report the error. And it never looked at
/// <see cref="AggregateException.InnerExceptions"/>, so a <c>Task.WhenAll</c> failure with three
/// faults reported only the first and silently dropped the other two.
/// </para>
/// </summary>
public sealed class ExceptionProjectionTests
{
    [Fact]
    public void A_chain_shorter_than_the_limit_is_not_marked_truncated()
    {
        var chain = BuildChain(3);

        var detail = ExceptionDetail.FromException(chain);

        AssertChainDepth(detail, expectedDepth: 3, anyTruncated: false);
    }

    [Fact]
    public void A_chain_deeper_than_the_limit_is_cut_off_and_says_so()
    {
        // Well past ExceptionDetail.MaximumDepth (8), so this also stands in for a cycle: nothing
        // in the public Exception API lets a test construct an actual InnerException loop (the
        // field has no public setter), but the bound that matters is the same either way - the
        // walk must stop after a fixed number of levels no matter how far the chain actually
        // goes, and this proves that without relying on undocumented reflection into BCL fields
        // that could differ between .NET 9 and .NET Framework.
        var chain = BuildChain(ExceptionDetail.MaximumDepth + 40);

        var detail = ExceptionDetail.FromException(chain);

        // Walk until we hit the node marked Truncated, and confirm it took exactly MaximumDepth
        // steps to get there - not "eventually", not "sooner because something else gave up".
        var depth = 0;
        var current = detail;
        while (!current.Truncated)
        {
            Assert.NotNull(current.InnerException);
            current = current.InnerException;
            depth++;
            Assert.True(depth <= ExceptionDetail.MaximumDepth, "Truncation did not happen at the configured depth.");
        }

        Assert.Equal(ExceptionDetail.MaximumDepth, depth);
        Assert.Null(current.InnerException);
        Assert.Null(current.InnerExceptions);
    }

    [Fact]
    public void Every_fault_of_an_AggregateException_is_reported()
    {
        var aggregate = new AggregateException(
            new InvalidOperationException("first"),
            new ArgumentException("second"),
            new NotSupportedException("third"));

        var detail = ExceptionDetail.FromException(aggregate);

        Assert.NotNull(detail.InnerExceptions);
        Assert.Equal(3, detail.InnerExceptions!.Count);
        Assert.Equal(
            new[] { "first", "second", "third" },
            detail.InnerExceptions.Select(fault => fault.Message));
        Assert.Null(detail.DroppedInnerExceptions);

        // A reader that only knows about the single-child shape still gets the first fault
        // rather than nothing.
        Assert.NotNull(detail.InnerException);
        Assert.Equal("first", detail.InnerException!.Message);
    }

    [Fact]
    public void Faults_beyond_the_limit_are_dropped_and_the_count_is_reported()
    {
        var faults = Enumerable.Range(0, ExceptionDetail.MaximumDepth + 2)
            .Select(index => (Exception)new InvalidOperationException($"fault-{index}"))
            .ToArray();
        var aggregate = new AggregateException(faults);

        var detail = ExceptionDetail.FromException(aggregate);

        Assert.NotNull(detail.InnerExceptions);
        Assert.Equal(ExceptionDetail.MaximumDepth, detail.InnerExceptions!.Count);
        Assert.Equal(2, detail.DroppedInnerExceptions);
        // The faults that were kept are the first ones in order, not an arbitrary subset.
        Assert.Equal("fault-0", detail.InnerExceptions[0].Message);
        Assert.Equal(
            $"fault-{ExceptionDetail.MaximumDepth - 1}",
            detail.InnerExceptions[detail.InnerExceptions.Count - 1].Message);
    }

    [Fact]
    public void A_long_message_is_truncated_to_the_configured_length()
    {
        var longMessage = new string('x', ExceptionDetail.MaximumMessageLength + 500);
        var exception = new InvalidOperationException(longMessage);

        var detail = ExceptionDetail.FromException(exception);

        Assert.Equal(ExceptionDetail.MaximumMessageLength, detail.Message.Length);
        Assert.Equal(longMessage.Substring(0, ExceptionDetail.MaximumMessageLength), detail.Message);
    }

    [Fact]
    public void A_short_message_is_left_untouched()
    {
        var detail = ExceptionDetail.FromException(new InvalidOperationException("short"));

        Assert.Equal("short", detail.Message);
    }

    [Fact]
    public void A_long_stack_trace_is_truncated_to_the_configured_length()
    {
        // .NET Framework's frame text is shorter than .NET 9's (no full source path in this
        // build), so 400 frames was not always enough there; 2000 clears both comfortably while
        // staying well short of the default 1 MB thread stack.
        Exception thrown;
        try
        {
            RecurseThenThrow(2000);
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        // The precondition: prove the raw stack trace actually needed truncating, so a pass here
        // means the cap engaged rather than never being exercised.
        Assert.True(
            thrown.StackTrace is { Length: > ExceptionDetail.MaximumStackTraceLength },
            "The test did not generate a stack trace long enough to exercise truncation.");

        var detail = ExceptionDetail.FromException(thrown);

        Assert.NotNull(detail.StackTrace);
        Assert.Equal(ExceptionDetail.MaximumStackTraceLength, detail.StackTrace!.Length);
        Assert.Equal(
            thrown.StackTrace!.Substring(0, ExceptionDetail.MaximumStackTraceLength),
            detail.StackTrace);
    }

    /// <summary>
    /// Left uncaught at every level so the propagating exception's own <c>StackTrace</c>
    /// accumulates one line per unwound frame, rather than restarting at each level the way a
    /// catch-and-rethrow would.
    /// </summary>
    private static void RecurseThenThrow(int remaining)
    {
        if (remaining <= 0)
        {
            throw new InvalidOperationException("deep");
        }

        RecurseThenThrow(remaining - 1);

        // Without this, the recursive call above is in tail position and a Release JIT - .NET
        // Framework's more so than .NET 9's - can compile it as an actual tail call, which
        // reuses the current frame instead of pushing a new one. That collapses the stack and
        // defeats the entire point of this helper, silently and only in Release.
        GC.KeepAlive(remaining);
    }

    private static Exception BuildChain(int depth)
    {
        Exception current = new InvalidOperationException("root");
        for (var index = 0; index < depth; index++)
        {
            current = new InvalidOperationException($"level-{index}", current);
        }

        return current;
    }

    private static void AssertChainDepth(ExceptionDetail detail, int expectedDepth, bool anyTruncated)
    {
        var depth = 0;
        var current = detail;
        var sawTruncated = false;
        while (current.InnerException is { } inner)
        {
            sawTruncated |= current.Truncated;
            current = inner;
            depth++;
        }

        sawTruncated |= current.Truncated;

        Assert.Equal(expectedDepth, depth);
        Assert.Equal(anyTruncated, sawTruncated);
    }
}
