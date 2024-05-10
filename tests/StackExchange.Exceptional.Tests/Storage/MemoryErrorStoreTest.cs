using System.Runtime.CompilerServices;
using StackExchange.Exceptional.Stores;
using Xunit.Abstractions;

namespace StackExchange.Exceptional.Tests.Storage;

public class MemoryErrorStoreTest(ITestOutputHelper output) : StoreBaseTest(output)
{
    protected override bool StoreHardDeletes => true;

    protected override ErrorStore GetStore([CallerMemberName]string appName = null) =>
        new MemoryErrorStore(new ErrorStoreSettings
        {
            ApplicationName = appName
        });
}
