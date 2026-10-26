using Xunit;

namespace WDM.Tests;

/// <summary>
/// Tests that mutate process-wide state (environment variables, current
/// directory, installed binaries) must not run while other collections are
/// resolving paths, or their temporary values leak into unrelated tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "ProcessState";
}
