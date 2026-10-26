using Xunit;

namespace WDM.Tests;

/// <summary>
/// <see cref="WDM.Services.Chunking.OriginController"/> is process-wide state
/// keyed by scheme+host+port. Separate test classes that drive the range engine
/// all use the same fake hosts, so when they run in parallel one class's 503/429
/// retries halve the shared budget and arm a backoff that the other class then
/// waits on — the suite can stall for minutes. These classes therefore share one
/// collection that never runs alongside other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EngineStateCollection
{
    public const string Name = "EngineState";
}
