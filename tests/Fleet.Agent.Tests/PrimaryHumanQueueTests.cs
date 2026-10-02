using Fleet.Agent.Models;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

public sealed partial class PrimaryHumanQueueTests
{
    private static QueuedMessagePart Part(string text, TaskPriority priority = TaskPriority.Routine,
        TaskSource source = TaskSource.UserMessage) => new(text, text, true, source,
        null, null, null, null, null, 0, DateTimeOffset.UnixEpoch, "synthetic", Priority: priority);

    private static QueuedMessage Add(DispatchQueue queue, long chat, string text,
        TaskPriority priority = TaskPriority.Routine, TaskSource source = TaskSource.UserMessage)
    {
        Assert.True(queue.TryEnqueue(chat, Part(text, priority, source), out var entry, out _));
        return entry!;
    }

    private static string[] Drain(DispatchQueue queue)
    {
        var tasks = new List<string>();
        while (queue.TryDequeue(out var item)) tasks.Add(item!.Task);
        return tasks.ToArray();
    }

    [Fact]
    public void Q1_Q2_PriorityIsFifoAheadOfRoutine()
    {
        var q = new DispatchQueue();
        Add(q, 1, "R1"); Add(q, 2, "R2");
        Add(q, 3, "P1", TaskPriority.PrimaryHuman); Add(q, -4, "P2", TaskPriority.PrimaryHuman);
        Assert.Equal(["P1", "P2", "R1", "R2"], Drain(q));
    }

    [Fact]
    public void Q4_ThreePriorityDispatchesThenRoutine()
    {
        var q = new DispatchQueue();
        Add(q, 1, "R1");
        for (var i = 1; i <= 5; i++) Add(q, i + 10, $"P{i}", TaskPriority.PrimaryHuman);
        Assert.Equal(["P1", "P2", "P3", "R1", "P4", "P5"], Drain(q));
    }

    [Fact]
    public void Q5_MergePromotesAllChatEntries()
    {
        var q = new DispatchQueue(); Add(q, 1, "R1");
        var u = Add(q, -2, "U1");
        Assert.True(q.TryAppend(-2, Part("P", TaskPriority.PrimaryHuman)));
        Assert.Equal(2, u.PartCount);
        Assert.Equal(TaskPriority.PrimaryHuman, u.Priority);
        Assert.Equal(["U1", "R1"], Drain(q));
    }

    [Fact]
    public void Q6_FullPartEntryMovesBeforeTheFreshEntry()
    {
        var q = new DispatchQueue(); Add(q, 1, "R1");
        Add(q, -2, "U1");
        for (var i = 1; i < 10; i++) Assert.True(q.TryAppend(-2, Part($"U{i + 1}")));
        Assert.False(q.TryAppend(-2, Part("P", TaskPriority.PrimaryHuman)));
        Add(q, -2, "P", TaskPriority.PrimaryHuman);
        Assert.Equal(["U1", "P", "R1"], Drain(q));
    }

    [Theory]
    [InlineData(TaskSource.UserMessage, 2)]
    [InlineData(TaskSource.NewCommand, 1)]
    public void Q6b_Q18_PromotionIsAllOrNothing(TaskSource source, int priorEntries)
    {
        var counter = new QueueLaneCounter(); var q = new DispatchQueue(counter);
        for (var i = 0; i < 9; i++) Add(q, 100 + i, $"P{i}", TaskPriority.PrimaryHuman);
        for (var i = 0; i < priorEntries; i++) Add(q, -2, $"U{i}", source: TaskSource.NewCommand);
        var added = Add(q, -2, "new", TaskPriority.PrimaryHuman, source);
        Assert.Equal(TaskPriority.Routine, added.Priority);
        Assert.Equal(priorEntries + 1, q.Snapshot().Count(e => e.ChatId == -2));
        Assert.All(q.Snapshot().Where(e => e.ChatId == -2), e => Assert.Equal(TaskPriority.Routine, e.Priority));
        Assert.Equal(1, counter.Count("promotion_refused_full"));
    }

    [Theory]
    [InlineData(TaskSource.DebouncedGroupBatch)]
    [InlineData(TaskSource.Relay)]
    [InlineData(TaskSource.Bridge)]
    [InlineData(TaskSource.CheckIn)]
    public void Q7_Q12_NonHumanSourcesCannotBePriority(TaskSource source)
    {
        var q = new DispatchQueue();
        Assert.Equal(TaskPriority.Routine, Add(q, 1, "other", TaskPriority.PrimaryHuman, source).Priority);
    }

    [Fact]
    public void Q8_Q10_Q10b_BlankKeyPositionsAndOverflowStayBounded()
    {
        var counter = new QueueLaneCounter(); var q = new DispatchQueue(counter);
        for (var i = 0; i < 10; i++) Add(q, 100 + i, $"P{i}", TaskPriority.PrimaryHuman);
        Assert.True(q.TryEnqueue(200, Part("overflow", TaskPriority.PrimaryHuman), out var overflow, out var position));
        Assert.Equal(TaskPriority.Routine, overflow!.Priority); Assert.Equal(11, position);
        Assert.Equal(1, counter.Count("priority_overflow_to_routine"));
        for (var i = 1; i < 20; i++) Add(q, 200 + i, $"R{i}");
        Assert.False(q.TryEnqueue(999, Part("full", TaskPriority.PrimaryHuman), out _, out _));
        var blank = new DispatchQueue();
        for (var i = 1; i <= 20; i++)
        { Assert.True(blank.TryEnqueue(i, Part($"R{i}"), out _, out var p)); Assert.Equal(i, p); }
        Assert.False(blank.TryEnqueue(21, Part("full"), out _, out _));
    }

    [Fact]
    public void Q10b_PositionsUseBothLanesForRoutineOnly()
    {
        var q = new DispatchQueue(); Add(q, 1, "P1", TaskPriority.PrimaryHuman); Add(q, 2, "P2", TaskPriority.PrimaryHuman);
        for (var i = 0; i < 3; i++) Add(q, 10 + i, $"R{i}");
        Assert.True(q.TryEnqueue(20, Part("routine"), out _, out var r)); Assert.Equal(6, r);
        Assert.True(q.TryEnqueue(21, Part("primary", TaskPriority.PrimaryHuman), out _, out var p)); Assert.Equal(3, p);
    }

    [Fact]
    public void Q16_Q17_NewNeverMergesAndPromotesInChatOrder()
    {
        var q = new DispatchQueue(); Add(q, 1, "R1"); Add(q, -2, "U1");
        Assert.False(q.TryAppend(-2, Part("new", TaskPriority.PrimaryHuman, TaskSource.NewCommand)));
        Add(q, -2, "new", TaskPriority.PrimaryHuman, TaskSource.NewCommand);
        Assert.Equal(["U1", "new", "R1"], Drain(q));
        q = new DispatchQueue(); Add(q, 1, "R1"); Add(q, -2, "new", source: TaskSource.NewCommand);
        Add(q, -2, "mention", TaskPriority.PrimaryHuman);
        Assert.Equal(["new", "mention", "R1"], Drain(q));
    }

    [Fact]
    public void Q13_DoublePromotionDoesNotChangeSequence()
    {
        var q = new DispatchQueue(); var entry = Add(q, 1, "first", TaskPriority.PrimaryHuman);
        var seq = entry.Seq;
        Assert.True(q.TryAppend(1, Part("second", TaskPriority.PrimaryHuman)));
        Assert.Equal(seq, entry.Seq); Assert.Equal(TaskPriority.PrimaryHuman, entry.Priority);
    }
}
