using FluentAssertions;
using MemoryService.Recall;

namespace MemoryService.Recall.Tests;

public sealed class MultiHopExpanderTests : IClassFixture<MultiHopExpanderTestFixture>, IAsyncLifetime
{
    private readonly MultiHopExpanderTestFixture _fx;

    public MultiHopExpanderTests(MultiHopExpanderTestFixture fx) => _fx = fx;

    public Task InitializeAsync() => _fx.ResetGraphAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Empty_seeds_returns_empty()
    {
        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([], maxHops: 2, default);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Zero_hops_returns_empty()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b);
        await _fx.SeedEdgeAsync(a, b);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(a, 1.0)], maxHops: 0, default);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task No_edges_returns_empty()
    {
        var a = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(a, 1.0)], maxHops: 2, default);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Hop1_score_is_seed_times_decay()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b);
        await _fx.SeedEdgeAsync(a, b);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(a, 1.0)], maxHops: 2, default);

        result.Single(r => r.Id == b).Score.Should().BeApproximately(0.6, 1e-9);
    }

    /// <summary>
    /// Regression test for the score-decay compounding bug.
    /// Path a → b → c. Hop-2 score for c MUST be 1.0 × 0.36, NOT 1.0 × 0.6 × 0.36 = 0.216.
    /// </summary>
    [Fact]
    public async Task Hop2_score_is_seed_times_decay_squared_not_compounded()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b, c);
        await _fx.SeedEdgeAsync(a, b);
        await _fx.SeedEdgeAsync(b, c);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(a, 1.0)], maxHops: 2, default);

        result.Single(r => r.Id == c).Score.Should().BeApproximately(0.36, 1e-9);
    }

    /// <summary>
    /// Regression test for the seed-leakage bug at hop ≥ 2.
    /// Both a and b are seeds, connected via c: a → c → b.
    /// At hop 2 the BFS reaches `b` from `a` (and vice versa). Neither seed must appear in output.
    /// </summary>
    [Fact]
    public async Task Original_seeds_never_appear_in_output()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b, c);
        await _fx.SeedEdgeAsync(a, c);
        await _fx.SeedEdgeAsync(c, b);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync(
            [new RankedMemory(a, 1.0), new RankedMemory(b, 0.5)],
            maxHops: 2,
            default);

        result.Should().NotContain(r => r.Id == a);
        result.Should().NotContain(r => r.Id == b);
        result.Should().Contain(r => r.Id == c);
    }

    /// <summary>
    /// BFS keeps a node at its closest hop. With a → b, a → c, and b → c,
    /// c is reachable at hop 1 directly AND at hop 2 via b. The hop-1 score (0.6) wins.
    /// </summary>
    [Fact]
    public async Task Bfs_keeps_node_at_closest_hop()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b, c);
        await _fx.SeedEdgeAsync(a, b);
        await _fx.SeedEdgeAsync(a, c);
        await _fx.SeedEdgeAsync(b, c);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(a, 1.0)], maxHops: 2, default);

        result.Single(r => r.Id == c).Score.Should().BeApproximately(0.6, 1e-9);
    }

    /// <summary>
    /// When two seeds reach the same node, the higher seed score wins.
    /// seedA (0.5) → m and seedB (0.1) → m → m gets 0.5 × 0.6 = 0.3.
    /// </summary>
    [Fact]
    public async Task Best_seed_score_wins_when_paths_converge()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var m = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b, m);
        await _fx.SeedEdgeAsync(a, m);
        await _fx.SeedEdgeAsync(b, m);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync(
            [new RankedMemory(a, 0.5), new RankedMemory(b, 0.1)],
            maxHops: 1,
            default);

        result.Single(r => r.Id == m).Score.Should().BeApproximately(0.3, 1e-9);
    }

    /// <summary>
    /// Edges are walked in both directions: an edge {x → seed} discovers x from a seed on the dst side.
    /// </summary>
    [Fact]
    public async Task Edge_traversal_is_bidirectional()
    {
        var seed = Guid.NewGuid(); var x = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(seed, x);
        await _fx.SeedEdgeAsync(x, seed);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(seed, 1.0)], maxHops: 1, default);

        result.Should().ContainSingle(r => r.Id == x);
        result.Single().Score.Should().BeApproximately(0.6, 1e-9);
    }

    /// <summary>maxHops bounds the BFS. With a → b → c → d and maxHops=2, d (at hop 3) is excluded.</summary>
    [Fact]
    public async Task MaxHops_excludes_nodes_beyond_limit()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();
        await _fx.SeedMemoriesAsync(a, b, c, d);
        await _fx.SeedEdgeAsync(a, b);
        await _fx.SeedEdgeAsync(b, c);
        await _fx.SeedEdgeAsync(c, d);

        await using var db = _fx.NewContext();
        var sut = new MultiHopExpander(db);

        var result = await sut.ExpandAsync([new RankedMemory(a, 1.0)], maxHops: 2, default);
        var ids = result.Select(r => r.Id).ToHashSet();

        ids.Should().Contain(b);
        ids.Should().Contain(c);
        ids.Should().NotContain(d);
    }
}
