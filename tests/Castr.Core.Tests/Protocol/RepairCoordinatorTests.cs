using Castr.Core.Protocol;
using Castr.Core.Time;
using Castr.Core.Transport;

namespace Castr.Core.Tests.Protocol;

public class RepairCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-07-24T00:00:00Z");
    private static readonly Endpoint OriginalSender = new("sender-host", 9000);

    /// <summary>
    /// Options with <b>both</b> randomized timing sources switched off — <see cref="RepairOptions.InitialRequestJitter"/>
    /// and <see cref="RepairOptions.RetryJitterFraction"/>. Used by every test whose subject is something other
    /// than the jitter itself (target ranking, in-flight suppression, backoff growth), so their assertions stay
    /// about exactly what they were about before the jitter existed.
    ///
    /// <para>Both matter, and leaving either on makes such a test genuinely flaky rather than merely imprecise:
    /// the shipped 500 ms first-request deferral would suppress the first plan outright, and the retry fraction
    /// stretches a 5 s timeout to up to 6.25 s against a default-seeded <see cref="Random"/> — enough to make
    /// "advance 6 s, expect a replan" fail about one run in five. The jitters' own behavior is covered by the
    /// <c>InitialRequestJitter_*</c> and <c>RetryJitter_*</c> tests below, with explicit seeds.</para>
    /// </summary>
    private static RepairOptions NoJitter(TimeSpan? requestTimeout = null) =>
        new(requestTimeout ?? TimeSpan.FromSeconds(5), RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero);

    [Fact]
    public void PlanRepairs_NoPeersAvailable_FallsBackToOriginalSender()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, NoJitter());

        var plans = coordinator.PlanRepairs(fileIndex: 0, missingChunkIndices: [1, 2, 3], OriginalSender, NonceFactory);

        Assert.Single(plans);
        Assert.Equal(OriginalSender, plans[0].Target);
        Assert.Equal([1, 2, 3], plans[0].ChunkIndices);
    }

    [Fact]
    public void PlanRepairs_PeerHasChunk_PrefersPeerOverOriginalSender()
    {
        var clock = new FakeClock(Epoch);
        var peerTable = new PeerTable();
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(1), 0, [0b0000_0010], "peer-1", 5000), Epoch); // has chunk 1

        var coordinator = new RepairCoordinator(peerTable, clock, NoJitter());
        var plans = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);

        Assert.Single(plans);
        Assert.Equal(new Endpoint("peer-1", 5000), plans[0].Target);
    }

    [Fact]
    public void PlanRepairs_MixOfPeerAndFallback_SplitsAcrossTargets()
    {
        var clock = new FakeClock(Epoch);
        var peerTable = new PeerTable();
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(1), 0, [0b0000_0001], "peer-1", 5000), Epoch); // only chunk 0

        var coordinator = new RepairCoordinator(peerTable, clock, NoJitter());
        var plans = coordinator.PlanRepairs(0, [0, 5], OriginalSender, NonceFactory); // chunk 5 has no peer

        Assert.Equal(2, plans.Count);
        Assert.Contains(plans, p => p.Target == new Endpoint("peer-1", 5000) && p.ChunkIndices.Contains(0));
        Assert.Contains(plans, p => p.Target == OriginalSender && p.ChunkIndices.Contains(5));
    }

    [Fact]
    public void PlanRepairs_ChunkAlreadyInFlight_IsNotReplannedBeforeTimeout()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, NoJitter());

        var first = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        Assert.Single(first);
        // Planning alone no longer marks anything in flight — only a confirmed send does (MarkRequested).
        coordinator.MarkRequested(0, first[0].ChunkIndices, clock.UtcNow);

        clock.Advance(TimeSpan.FromSeconds(2)); // still within timeout
        var second = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);

        Assert.Empty(second);
    }

    [Fact]
    public void PlanRepairs_RequestTimesOut_IsReplanned()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, NoJitter());

        var first = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        coordinator.MarkRequested(0, first[0].ChunkIndices, clock.UtcNow);
        clock.Advance(TimeSpan.FromSeconds(6)); // past timeout (first attempt => no backoff multiplier yet)

        var replanned = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);

        Assert.Single(replanned);
    }

    [Fact]
    public void MarkFulfilled_AllowsImmediateReplanning_WithoutWaitingForTimeout()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, NoJitter());

        var first = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        coordinator.MarkRequested(0, first[0].ChunkIndices, clock.UtcNow);
        Assert.True(coordinator.IsPending(0, 1));

        coordinator.MarkFulfilled(0, 1);

        Assert.False(coordinator.IsPending(0, 1));
        var replanned = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        Assert.Single(replanned); // e.g. it turned out corrupt on arrival and is missing again
    }

    [Fact]
    public void PlanRepairs_NoMissingChunks_ReturnsEmpty()
    {
        var coordinator = new RepairCoordinator(new PeerTable(), new FakeClock(Epoch));

        Assert.Empty(coordinator.PlanRepairs(0, [], OriginalSender, NonceFactory));
    }

    [Fact]
    public void PlanRepairs_MultiplePeersWithSameCompleteness_JitterPicksAmongThem_Deterministically()
    {
        var clock = new FakeClock(Epoch);
        var peerTable = new PeerTable();
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(1), 0, [0b0000_0001], "peer-1", 1), Epoch);
        peerTable.Observe(new PeerHaveMessage(SessionId(), PeerId(2), 0, [0b0000_0001], "peer-2", 1), Epoch);

        var coordinatorA = new RepairCoordinator(peerTable, clock, NoJitter(), new Random(123));
        var coordinatorB = new RepairCoordinator(peerTable, clock, NoJitter(), new Random(123));

        var planA = coordinatorA.PlanRepairs(0, [0], OriginalSender, NonceFactory);
        var planB = coordinatorB.PlanRepairs(0, [0], OriginalSender, NonceFactory);

        Assert.Equal(planA[0].Target, planB[0].Target); // same seed => same jitter choice
    }

    // ---- M7 P0: single-datagram request cap, mark-after-send, exponential backoff, jitter ----

    [Fact]
    public void MaxChunksPerRequestFor_KeepsEveryPlannedRequest_InsideOneDatagram()
    {
        // The cap is only meaningful if it is derived from the encoding rather than guessed, so assert against
        // MessageCodec itself. The cap deliberately sits BELOW the true single-datagram maximum because
        // ChunkRequestHeadroom is reserved for a future non-empty ReturnHost — so cap+1 still fits today, and
        // that margin is the intent, not an off-by-one.
        const int budget = WirePacketizer.DefaultMaxDatagramPayload;
        int cap = RepairOptions.MaxChunksPerRequestFor(budget);

        Assert.Equal(268, cap);
        Assert.Equal(RepairOptions.DefaultMaxChunksPerRequest, cap);

        Assert.True(EncodedRequestSize(cap) <= budget, $"a {cap}-index request must fit in one datagram");
        Assert.Single(WirePacketizer.Fragment(EncodedRequest(cap), budget)); // therefore never fragmented

        // Pin the actual cliff, and pin that the reserved headroom really is headroom rather than luck.
        int trueMax = Enumerable.Range(1, 400).Last(n => EncodedRequestSize(n) <= budget);
        Assert.Equal(284, trueMax);
        Assert.True(cap < trueMax, "the cap must leave headroom below the true single-datagram maximum");
        Assert.True(EncodedRequestSize(trueMax + 1) > budget, "one past the true maximum must not fit");
    }

    [Fact]
    public void PlanRepairs_MoreMissingThanTheCap_SplitsIntoSingleDatagramRequests_EachWithItsOwnNonce()
    {
        var coordinator = new RepairCoordinator(
            new PeerTable(), new FakeClock(Epoch),
            new RepairOptions(TimeSpan.FromSeconds(5), MaxChunksPerRequest: 10, InitialRequestJitter: TimeSpan.Zero));

        var plans = coordinator.PlanRepairs(0, [.. Enumerable.Range(0, 25)], OriginalSender, NonceFactory, maxRequests: 10);

        Assert.Equal(3, plans.Count); // 10 + 10 + 5
        Assert.All(plans, p => Assert.True(p.ChunkIndices.Length <= 10));
        Assert.Equal(25, plans.Sum(p => p.ChunkIndices.Length)); // nothing dropped, only batched
        Assert.Equal([.. Enumerable.Range(0, 25)], plans.SelectMany(p => p.ChunkIndices).Order());
        Assert.Equal(3, plans.Select(p => Convert.ToHexString(p.RequestNonce)).Distinct().Count());
    }

    [Fact]
    public void PlanRepairs_AtDefaultOptions_NeverPlansARequestThatWouldFragment()
    {
        var coordinator = new RepairCoordinator(new PeerTable(), new FakeClock(Epoch), NoJitter());

        // A cold-start-sized miss set: before the cap this produced ONE ~39.7 KB request spread over ~35
        // all-or-nothing fragments, so losing any single fragment cost a full RequestTimeout of silence.
        // maxRequests lifted here deliberately, so this test is about the per-REQUEST cap in isolation; the
        // per-PASS cap has its own tests below.
        var plans = coordinator.PlanRepairs(
            0, [.. Enumerable.Range(0, 9_900)], OriginalSender, NonceFactory, maxRequests: int.MaxValue);

        Assert.All(plans, p => Assert.Single(
            WirePacketizer.Fragment(EncodedRequest(p.ChunkIndices.Length), WirePacketizer.DefaultMaxDatagramPayload)));
        Assert.Equal(9_900, plans.Sum(p => p.ChunkIndices.Length));
    }

    // ---- per-pass request cap: the actual amplification bound ----

    [Fact]
    public void PlanRepairs_HugeMissSet_IsBoundedByMaxRequestsPerPass_NotJustPerRequest()
    {
        // The per-request cap alone still let a cold-start pass emit ~38 single-datagram requests, and the sender
        // still re-served every chunk in all of them — fixing the fragmentation hazard but bounding nothing. This
        // is the bound that actually limits amplification, and it is what stops the carousel valve from being the
        // only thing standing between the system and a repair storm.
        var coordinator = new RepairCoordinator(
            new PeerTable(), new FakeClock(Epoch),
            new RepairOptions(TimeSpan.FromSeconds(5), MaxRequestsPerPass: 4, InitialRequestJitter: TimeSpan.Zero));

        var plans = coordinator.PlanRepairs(0, [.. Enumerable.Range(0, 9_900)], OriginalSender, NonceFactory);

        Assert.Equal(4, plans.Count);
        Assert.Equal(4 * RepairOptions.DefaultMaxChunksPerRequest, plans.Sum(p => p.ChunkIndices.Length));
    }

    [Fact]
    public void PlanRepairs_DefaultOptions_AreBoundedToFourRequestsPerPass()
    {
        var coordinator = new RepairCoordinator(new PeerTable(), new FakeClock(Epoch), NoJitter());

        var plans = coordinator.PlanRepairs(0, [.. Enumerable.Range(0, 9_900)], OriginalSender, NonceFactory);

        Assert.Equal(RepairOptions.DefaultMaxRequestsPerPass, plans.Count);
    }

    [Fact]
    public void PlanRepairs_ExplicitBudget_OverridesTheOptionForOneCall()
    {
        // ReceiverSession spreads one pass's budget across files by passing whatever it has left.
        var coordinator = new RepairCoordinator(new PeerTable(), new FakeClock(Epoch), NoJitter());

        Assert.Equal(2, coordinator.PlanRepairs(0, [.. Enumerable.Range(0, 9_900)], OriginalSender, NonceFactory, 2).Count);
        Assert.Empty(coordinator.PlanRepairs(0, [.. Enumerable.Range(0, 9_900)], OriginalSender, NonceFactory, 0));
    }

    [Fact]
    public void PlanRepairs_SuccessivePasses_MakeProgressThroughALargeMissSet()
    {
        // The per-pass cap must throttle, not stall: consecutive passes have to keep covering new ground, and
        // must never re-plan a chunk that is still in flight.
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(TimeSpan.FromSeconds(5), MaxChunksPerRequest: 10, MaxRequestsPerPass: 2, InitialRequestJitter: TimeSpan.Zero));

        var missing = Enumerable.Range(0, 100).ToList();
        var covered = new HashSet<int>();
        for (int pass = 0; pass < 5; pass++)
        {
            var plans = coordinator.PlanRepairs(0, missing, OriginalSender, NonceFactory);
            Assert.Equal(2, plans.Count);
            foreach (var plan in plans)
            {
                foreach (var index in plan.ChunkIndices)
                    Assert.True(covered.Add(index), $"chunk {index} was planned twice while still in flight");
                coordinator.MarkRequested(0, plan.ChunkIndices, clock.UtcNow);
            }
        }

        Assert.Equal(100, covered.Count); // 5 passes x 2 requests x 10 indices, no overlap
    }

    [Fact]
    public void PlanRepairs_WithoutMarkRequested_IsImmediatelyReplannable()
    {
        // The point of moving marking out of PlanRepairs: a request that never reached the socket must not
        // suppress a retry for a whole RequestTimeout.
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, NoJitter());

        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
        Assert.False(coordinator.IsPending(0, 1)); // planning alone marks nothing

        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory)); // no 5 s penalty
    }

    [Fact]
    public void MarkRequested_ThenTimeout_GrowsTheEffectiveTimeoutExponentially_UpToTheCap()
    {
        var clock = new FakeClock(Epoch);
        var baseTimeout = TimeSpan.FromSeconds(5);
        // RetryJitterFraction 0 so the growth is exactly the documented power of two; the jitter has its own tests.
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(baseTimeout, MaxBackoffDoublings: 3, MaxRequestTimeout: TimeSpan.FromHours(1),
                RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

        Assert.Equal(TimeSpan.Zero, coordinator.EffectiveTimeout(0, 1) - baseTimeout); // 0 attempts => base

        var expected = new[] { 1, 2, 4, 8, 8, 8 }; // 2^min(attempts-1, 3), capped at 8x
        for (int attempt = 1; attempt <= expected.Length; attempt++)
        {
            coordinator.MarkRequested(0, [1], clock.UtcNow);
            Assert.Equal(attempt, coordinator.AttemptCount(0, 1));
            Assert.Equal(baseTimeout * expected[attempt - 1], coordinator.EffectiveTimeout(0, 1));

            // Age past the (grown) timeout so the next PlanRepairs re-offers it, exactly as a real retry round
            // would; the attempt history must survive that expiry or the backoff would reset every round.
            clock.Advance(coordinator.EffectiveTimeout(0, 1) + TimeSpan.FromSeconds(1));
            Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
        }
    }

    [Fact]
    public void Backoff_ChunkNobodyCanSupply_IsNotReRequestedAtTheBasePeriod()
    {
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(TimeSpan.FromSeconds(5), MaxBackoffDoublings: 4, RetryJitterFraction: 0,
                InitialRequestJitter: TimeSpan.Zero));

        // Two confirmed sends => effective timeout is 2x base, so 6 s (which would have re-planned before) is
        // no longer enough to re-offer it. This is the "stop hammering" property the wiki documents.
        coordinator.MarkRequested(0, [1], clock.UtcNow);
        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
        coordinator.MarkRequested(0, [1], clock.UtcNow);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Empty(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));

        clock.Advance(TimeSpan.FromSeconds(6)); // 12 s total > 10 s
        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
    }

    [Fact]
    public void MarkFulfilled_ResetsTheBackoff_SoAReRequestStartsFromTheBaseTimeout()
    {
        var clock = new FakeClock(Epoch);
        var baseTimeout = TimeSpan.FromSeconds(5);
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(baseTimeout, RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

        coordinator.MarkRequested(0, [1], clock.UtcNow);
        coordinator.MarkRequested(0, [1], clock.UtcNow);
        Assert.Equal(baseTimeout * 2, coordinator.EffectiveTimeout(0, 1));

        coordinator.MarkFulfilled(0, 1);

        Assert.Equal(0, coordinator.AttemptCount(0, 1));
        Assert.Equal(baseTimeout, coordinator.EffectiveTimeout(0, 1));
    }

    [Fact]
    public void EffectiveTimeout_BackoffIsClampedInWallClock_NotJustByDoublings()
    {
        // The backoff exists to stop hammering a chunk nobody can supply, but a timeout equally often means "my
        // request was dropped" — exactly the failure P0 makes cheap — so unbounded 16x growth would turn a few
        // unlucky drops into minute-scale silence on a chunk a peer could serve immediately.
        var clock = new FakeClock(Epoch);
        var baseTimeout = TimeSpan.FromSeconds(5);
        var clamp = TimeSpan.FromSeconds(12);
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(baseTimeout, MaxBackoffDoublings: 4, MaxRequestTimeout: clamp,
                RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

        for (int i = 0; i < 10; i++)
        {
            coordinator.MarkRequested(0, [1], clock.UtcNow);
            Assert.True(coordinator.EffectiveTimeout(0, 1) <= clamp,
                $"attempt {i + 1} gave {coordinator.EffectiveTimeout(0, 1)}, over the {clamp} clamp");
        }

        // Unclamped this would be 5 s * 2^4 = 80 s; clamped it never exceeds 12 s, so the chunk is still retried.
        Assert.Equal(clamp, coordinator.EffectiveTimeout(0, 1));
        clock.Advance(clamp + TimeSpan.FromSeconds(1));
        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
    }

    [Fact]
    public void DefaultOptions_BackoffLadderIsReachableAndBounded()
    {
        // Asserted as a range rather than an exact value, and across many seeds, so it is insensitive to the retry
        // jitter draw by construction instead of by margin. (An earlier version asserted equality against the
        // clamp with an unseeded Random — the same shape as a flake found earlier in this suite.)
        var options = RepairOptions.Default;
        var floor = options.EffectiveMaxRequestTimeout;
        var ceiling = options.EffectiveMaxRequestTimeout * (1 + options.RetryJitterFraction);

        for (int seed = 0; seed < 25; seed++)
        {
            var clock = new FakeClock(Epoch);
            var coordinator = new RepairCoordinator(new PeerTable(), clock, options, new Random(seed));

            for (int attempt = 0; attempt < 8; attempt++)
                coordinator.MarkRequested(0, [1], clock.UtcNow);

            // Clamped, but not collapsed onto the clamp: the jitter is applied AFTER the clamp precisely so
            // receivers do not converge into lockstep in this steady state.
            var effective = coordinator.EffectiveTimeout(0, 1);
            Assert.InRange(effective, floor, ceiling);
        }
    }

    [Fact]
    public void DefaultBackoffDoublings_AreAllReachableUnderTheDefaultClamp()
    {
        // Guards the reconciliation between MaxBackoffDoublings and MaxRequestTimeout: shipping 4 doublings under
        // a 20 s clamp meant steps 3 and 4 were dead configuration while the docs advertised a 16x ladder. Every
        // configured doubling must produce a distinct, actually-reachable timeout.
        var options = RepairOptions.Default with { RetryJitterFraction = 0, InitialRequestJitter = TimeSpan.Zero };
        var clock = new FakeClock(Epoch);
        var coordinator = new RepairCoordinator(new PeerTable(), clock, options);

        var ladder = new List<TimeSpan>();
        for (int attempt = 1; attempt <= options.MaxBackoffDoublings + 1; attempt++)
        {
            coordinator.MarkRequested(0, [1], clock.UtcNow);
            ladder.Add(coordinator.EffectiveTimeout(0, 1));
        }

        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], ladder);
        Assert.Equal(options.EffectiveMaxRequestTimeout, ladder[^1]); // the last step lands exactly on the clamp
        Assert.Equal(ladder.Count, ladder.Distinct().Count());        // no dead steps
    }

    // ---- randomized jitter: per-chunk, wall-clock, on the injected clock and Random ----

    [Fact]
    public void InitialRequestJitter_BoundsTheDeferral_SoTheFirstRequestAlwaysEventuallyFires()
    {
        // The deferral is randomized, so whether any single receiver fires on its very first pass depends on its
        // draw (that variability is the point — see the spread test below). What must hold unconditionally is the
        // bound: once the whole jitter window has elapsed, every receiver is definitely requestable. Asserted
        // across many seeds so this cannot pass by luck of one draw.
        var jitter = TimeSpan.FromMilliseconds(500);

        for (int seed = 0; seed < 25; seed++)
        {
            var clock = new FakeClock(Epoch);
            var coordinator = new RepairCoordinator(
                new PeerTable(), clock, new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: jitter),
                new Random(seed));

            coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory); // draws this chunk's deferral
            clock.Advance(jitter);

            Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
        }
    }

    [Fact]
    public void InitialRequestJitter_DoesDeferSomeReceivers_RatherThanFiringForAllImmediately()
    {
        // The complement of the bound above: a real spread means a meaningful share of receivers do NOT fire on
        // their first pass. If they all did, the jitter would be inert — which is exactly the defect this
        // replaced (a pass counter that burned down before the watermark ever released anything).
        var jitter = TimeSpan.FromMilliseconds(500);
        int deferred = 0;

        for (int seed = 0; seed < 60; seed++)
        {
            var clock = new FakeClock(Epoch);
            var coordinator = new RepairCoordinator(
                new PeerTable(), clock, new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: jitter),
                new Random(seed));

            if (coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory).Count == 0)
                deferred++;
        }

        Assert.True(deferred > 30, $"only {deferred} of 60 receivers were deferred on their first pass");
    }

    [Fact]
    public void InitialRequestJitter_SpreadsFirstRequestsAcrossReceivers_NotOntoOneInstant()
    {
        // Model many receivers noticing the same lost chunk in the same instant. With a real spread, the number
        // that would fire on any single 50 ms tick is a fraction of the population; with an inert or fixed delay
        // they would all fire on the same one.
        var jitter = TimeSpan.FromMilliseconds(500);
        var firedAtTick = new List<int>();

        for (int seed = 0; seed < 60; seed++)
        {
            var clock = new FakeClock(Epoch);
            var coordinator = new RepairCoordinator(
                new PeerTable(), clock, new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: jitter),
                new Random(seed));

            for (int tick = 0; tick < 12; tick++)
            {
                if (coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory).Count > 0)
                {
                    firedAtTick.Add(tick);
                    break;
                }
                clock.Advance(TimeSpan.FromMilliseconds(50));
            }
        }

        Assert.Equal(60, firedAtTick.Count); // every receiver eventually asked
        Assert.True(firedAtTick.Distinct().Count() >= 5,
            $"first requests landed on only {firedAtTick.Distinct().Count()} distinct ticks — not a real spread");
        var busiestTick = firedAtTick.GroupBy(t => t).Max(g => g.Count());
        Assert.True(busiestTick < 30, $"{busiestTick} of 60 receivers fired on one tick — herd not suppressed");
    }

    [Fact]
    public void InitialRequestJitter_IsDeterministicForASeededRandom()
    {
        var options = new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.FromMilliseconds(500));

        static int TicksUntilFirstRequest(RepairOptions options, int seed)
        {
            var clock = new FakeClock(Epoch);
            var coordinator = new RepairCoordinator(new PeerTable(), clock, options, new Random(seed));
            for (int tick = 0; tick < 60; tick++)
            {
                if (coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory).Count > 0)
                    return tick;
                clock.Advance(TimeSpan.FromMilliseconds(10));
            }
            return -1;
        }

        Assert.Equal(TicksUntilFirstRequest(options, 99), TicksUntilFirstRequest(options, 99));
    }

    [Fact]
    public void InitialRequestJitter_Zero_RequestsImmediately()
    {
        var coordinator = new RepairCoordinator(
            new PeerTable(), new FakeClock(Epoch),
            new RepairOptions(TimeSpan.FromSeconds(5), InitialRequestJitter: TimeSpan.Zero));

        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
    }

    [Fact]
    public void InitialRequestJitter_IsNotReAppliedToARetry_OnlyToTheFirstRequest()
    {
        var clock = new FakeClock(Epoch);
        var jitter = TimeSpan.FromMilliseconds(500);
        var baseTimeout = TimeSpan.FromSeconds(5);
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(baseTimeout, RetryJitterFraction: 0, InitialRequestJitter: jitter), new Random(11));

        coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory); // draws the deferral
        clock.Advance(jitter);                                        // past it, so the first request fires
        var first = coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory);
        Assert.Single(first);
        coordinator.MarkRequested(0, first[0].ChunkIndices, clock.UtcNow);

        // A retry is gated by the (backed-off) request timeout, not by a fresh initial-jitter draw.
        clock.Advance(baseTimeout + TimeSpan.FromSeconds(1));
        Assert.Single(coordinator.PlanRepairs(0, [1], OriginalSender, NonceFactory));
    }

    [Fact]
    public void RetryJitter_SpreadsRetryTimeoutsAcrossReceivers()
    {
        // Backoff alone re-aligns every receiver onto the same doubling grid, so the herd re-converges on each
        // retry. A per-chunk multiplier redrawn per attempt is what de-synchronizes retries too.
        var baseTimeout = TimeSpan.FromSeconds(5);
        var timeouts = new HashSet<TimeSpan>();

        for (int seed = 0; seed < 30; seed++)
        {
            var clock = new FakeClock(Epoch);
            var coordinator = new RepairCoordinator(
                new PeerTable(), clock,
                new RepairOptions(baseTimeout, RetryJitterFraction: 0.25, InitialRequestJitter: TimeSpan.Zero),
                new Random(seed));

            coordinator.MarkRequested(0, [1], clock.UtcNow);
            var effective = coordinator.EffectiveTimeout(0, 1);

            // Always at least the base, never more than base * (1 + fraction).
            Assert.True(effective >= baseTimeout, $"{effective} is below the base timeout");
            Assert.True(effective <= baseTimeout * 1.25, $"{effective} exceeds base * 1.25");
            timeouts.Add(effective);
        }

        Assert.True(timeouts.Count > 5, $"only {timeouts.Count} distinct retry timeouts across 30 receivers");
    }

    [Fact]
    public void RetryJitter_Zero_GivesExactlyTheBackedOffTimeout()
    {
        var clock = new FakeClock(Epoch);
        var baseTimeout = TimeSpan.FromSeconds(5);
        var coordinator = new RepairCoordinator(
            new PeerTable(), clock,
            new RepairOptions(baseTimeout, RetryJitterFraction: 0, InitialRequestJitter: TimeSpan.Zero));

        coordinator.MarkRequested(0, [1], clock.UtcNow);

        Assert.Equal(baseTimeout, coordinator.EffectiveTimeout(0, 1));
    }

    private static byte[] EncodedRequest(int indexCount) => MessageCodec.Encode(new ChunkRequestMessage(
        new byte[16], new byte[16], new byte[16], 0, [.. Enumerable.Range(0, indexCount)], "", 0));

    private static int EncodedRequestSize(int indexCount) => EncodedRequest(indexCount).Length;

    private static byte[] NonceFactory() => Guid.NewGuid().ToByteArray();
    private static byte[] SessionId() => new byte[16];
    private static byte[] PeerId(byte fill)
    {
        var bytes = new byte[16];
        Array.Fill(bytes, fill);
        return bytes;
    }
}
