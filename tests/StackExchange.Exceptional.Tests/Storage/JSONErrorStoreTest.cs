using System.Runtime.CompilerServices;
using StackExchange.Exceptional.Stores;
using Xunit.Abstractions;

namespace StackExchange.Exceptional.Tests.Storage;

public class JSONErrorStoreTest(ITestOutputHelper output) : StoreBaseTest(output)
{
    protected override bool StoreHardDeletes => true;

    protected override ErrorStore GetStore([CallerMemberName]string appName = null) =>
        new JSONErrorStore(new ErrorStoreSettings
        {
            ApplicationName = appName,
            Path = GetUniqueFolder(),
            CreatePathIfMissing = true
        });

    protected static string GetUniqueFolder() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
}
