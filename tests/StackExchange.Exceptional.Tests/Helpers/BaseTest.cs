using StackExchange.Exceptional.Internal;
using Xunit;
using Xunit.Abstractions;

namespace StackExchange.Exceptional.Tests;

public abstract class BaseTest(ITestOutputHelper output)
{
    public const string NonParallel = nameof(NonParallel);

    protected ITestOutputHelper Output { get; } = output;
}

public class TestSettings : ExceptionalSettingsBase
{
    public TestSettings(ErrorStore store)
    {
        DefaultStore = store;
    }
}

[CollectionDefinition(BaseTest.NonParallel, DisableParallelization = true)]
public class NonParallelDefinition
{
}
