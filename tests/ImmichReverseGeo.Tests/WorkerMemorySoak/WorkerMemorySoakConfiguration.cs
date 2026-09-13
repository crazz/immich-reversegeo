using ImmichReverseGeo.Core.WorkerJobs;

namespace ImmichReverseGeo.Tests.WorkerMemorySoak;

internal enum SoakPhase { Warmup, Measured }
internal enum SoakCycle { Success, CooperativeCancellation, PrePublicationFailure }
internal sealed record SoakIteration(int Order, SoakPhase Phase, WorkerJobKind Kind, SoakCycle Cycle);

internal sealed record SoakJobWeights(int Processing, int Lookup, int Cache)
{
    internal int Total => Processing + Lookup + Cache;
}

internal sealed class WorkerMemorySoakConfiguration
{
    private const int MaximumIterations = 100_000;
    private const int MaximumWeight = 100;

    private WorkerMemorySoakConfiguration(int seed, int warmupIterations, int measuredIterations,
        SoakJobWeights weights, bool cancellation, bool failure)
    {
        Seed = seed;
        WarmupIterations = warmupIterations;
        MeasuredIterations = measuredIterations;
        Weights = weights;
        Cancellation = cancellation;
        Failure = failure;
    }

    internal int Seed { get; }
    internal int WarmupIterations { get; }
    internal int MeasuredIterations { get; }
    internal SoakJobWeights Weights { get; }
    internal bool Cancellation { get; }
    internal bool Failure { get; }

    internal static WorkerMemorySoakConfiguration Create(int seed = 6801, int warmupIterations = 5,
        int measuredIterations = 10, SoakJobWeights? weights = null, bool cancellation = false, bool failure = false)
    {
        weights ??= new(3, 1, 1);
        if (weights.Processing is < 1 or > MaximumWeight || weights.Lookup is < 1 or > MaximumWeight
            || weights.Cache is < 1 or > MaximumWeight || weights.Processing <= weights.Lookup + weights.Cache)
        {
            throw new ArgumentException("soak-config/invalid-job-weights");
        }
        if (warmupIterations is < 1 or > MaximumIterations || measuredIterations is < 1 or > MaximumIterations)
        {
            throw new ArgumentException("soak-config/invalid-iteration-count");
        }
        // Complete weighted rounds make the declared and actual proportions exact
        // and guarantee both minor job kinds in each phase, even for a short run.
        if (warmupIterations % weights.Total != 0 || measuredIterations % weights.Total != 0)
        {
            throw new ArgumentException("soak-config/incomplete-weighted-round");
        }
        if (failure && measuredIterations / weights.Total * weights.Cache < 2)
        {
            throw new ArgumentException("soak-config/failure-cycle-needs-successful-cache-peer");
        }
        return new(seed, warmupIterations, measuredIterations, weights, cancellation, failure);
    }

    internal IReadOnlyList<SoakIteration> CreateSequence()
    {
        var random = new Random(Seed);
        var sequence = new List<SoakIteration>(WarmupIterations + MeasuredIterations);
        AddPhase(SoakPhase.Warmup, WarmupIterations, random, sequence);
        AddPhase(SoakPhase.Measured, MeasuredIterations, random, sequence);
        return sequence.AsReadOnly();
    }

    private void AddPhase(SoakPhase phase, int count, Random random, List<SoakIteration> sequence)
    {
        int processing = 0;
        int cache = 0;
        for (int round = 0; round < count / Weights.Total; round++)
        {
            var kinds = Enumerable.Repeat(WorkerJobKind.ProcessAssets, Weights.Processing)
                .Concat(Enumerable.Repeat(WorkerJobKind.CoordinateLookup, Weights.Lookup))
                .Concat(Enumerable.Repeat(WorkerJobKind.CacheMutation, Weights.Cache)).ToArray();
            random.Shuffle(kinds);
            foreach (var kind in kinds)
            {
                var cycle = SoakCycle.Success;
                if (phase == SoakPhase.Measured)
                {
                    if (kind == WorkerJobKind.ProcessAssets && ++processing % 3 == 0 && Cancellation)
                    {
                        cycle = SoakCycle.CooperativeCancellation;
                    }
                    else if (kind == WorkerJobKind.CacheMutation && ++cache % 2 == 0 && Failure)
                    {
                        cycle = SoakCycle.PrePublicationFailure;
                    }
                }
                sequence.Add(new(sequence.Count + 1, phase, kind, cycle));
            }
        }
    }
}
