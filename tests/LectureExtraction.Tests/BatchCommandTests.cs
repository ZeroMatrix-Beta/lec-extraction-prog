using System.Linq;
using LectureExtraction.Cli.Commands;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the two decisions the batch supervisor makes on its own: which worker gets which videos,
/// and whether a worker spec is usable at all. Everything else it does is delegated to child
/// processes, so these are the only places it can be wrong.
/// </summary>
public class BatchWorkerSpecTests {
    [Fact]
    public void ParsesProfileAndModel() {
        Assert.True(BatchCommand.TryParseWorkers("profile=1:model=gemini-3.5-flash,profile=2", out var workers, out _));

        Assert.Equal(2, workers.Count);
        Assert.Equal(1, workers[0].Profile);
        Assert.Equal("gemini-3.5-flash", workers[0].Model);
        Assert.Equal(2, workers[1].Profile);
        Assert.Null(workers[1].Model);
    }

    [Fact]
    public void RejectsTwoWorkersOnOneProfile() {
        // They would share a rate-limit budget, making the batch slower than a single worker - the
        // exact outcome running in parallel is meant to avoid.
        Assert.False(BatchCommand.TryParseWorkers("profile=1,profile=1", out _, out string? error));
        Assert.Contains("rate-limit budget", error);
    }

    [Fact]
    public void RejectsAFieldThatIsNotKeyValue() {
        Assert.False(BatchCommand.TryParseWorkers("profile", out _, out string? error));
        Assert.Contains("key=value", error);
    }

    [Fact]
    public void RejectsAnUnknownField() {
        Assert.False(BatchCommand.TryParseWorkers("threads=4", out _, out string? error));
        Assert.Contains("Unknown worker field", error);
    }

    [Fact]
    public void RejectsANonNumericProfile() {
        Assert.False(BatchCommand.TryParseWorkers("profile=main", out _, out string? error));
        Assert.Contains("not a valid profile index", error);
    }

    [Fact]
    public void RejectsAnEmptySpec() {
        Assert.False(BatchCommand.TryParseWorkers("", out _, out string? error));
        Assert.Contains("No workers", error);
    }

    [Fact]
    public void AllowsAWorkerWithNoProfile() {
        // One worker on the configured default is legitimate; only duplicates are not.
        Assert.True(BatchCommand.TryParseWorkers("model=gemini-3.5-flash", out var workers, out _));
        Assert.Null(workers[0].Profile);
    }
}

public class BatchShardingTests {
    [Fact]
    public void DealsVideosRoundRobinSoShardsStayBalanced() {
        string[] videos = ["a", "b", "c", "d", "e"];

        var shards = BatchCommand.Shard(videos, 2);

        Assert.Equal(["a", "c", "e"], shards[0]);
        Assert.Equal(["b", "d"], shards[1]);
    }

    [Fact]
    public void EveryVideoIsAssignedExactlyOnce() {
        // The property that keeps two workers out of the same output folder.
        string[] videos = [.. Enumerable.Range(0, 17).Select(i => $"video-{i}")];

        var shards = BatchCommand.Shard(videos, 4);
        var assigned = shards.SelectMany(shard => shard).ToList();

        Assert.Equal(17, assigned.Count);
        Assert.Equal(17, assigned.Distinct().Count());
    }

    [Fact]
    public void MoreWorkersThanVideosLeavesEmptyShards() {
        var shards = BatchCommand.Shard(["only-one"], 3);

        Assert.Single(shards[0]);
        Assert.Empty(shards[1]);
        Assert.Empty(shards[2]);
    }
}
