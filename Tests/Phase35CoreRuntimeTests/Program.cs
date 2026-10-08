using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Google.Protobuf;
using Utils;
using Utils.Framework;
using System.Threading;
internal static partial class Program
{
    private static int assertions;
    private static int Main() { try {
        AssertDisabledSettingRecoveryEntries();
        AssertExtensionStorageCannotEscapeRoot();
        AssertApiOwnerRevocationAndProviderPolicy();
        AssertStructuredExtensionLoggerPreservesContext();
        Console.WriteLine("PASS Shared 4 cases; " + assertions + " assertions"); return 0;
    } catch(Exception error) { Console.Error.WriteLine(error); return 1; } }
    private static void AssertExtensionStorageCannotEscapeRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "PhinixStorageTests", Guid.NewGuid().ToString("N"));
        try
        {
            FileSystemExtensionStorageProvider storage = new FileSystemExtensionStorageProvider(root);
            string path = storage.GetStoragePath("..", "..\\outside.dat");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert(path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase), "Sanitized extension storage must remain inside its configured root.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void AssertApiOwnerRevocationAndProviderPolicy()
    {
        ExtensionApiRegistry registry = new ExtensionApiRegistry();
        registry.RegisterApi<ISingleProviderApi>("owner-a", new SingleProviderApi());
        registry.RegisterApi<ISingleProviderApi>("owner-b", new SingleProviderApi());
        Assert(registry.ResolveAll<ISingleProviderApi>().Count == 1, "Single-provider API contracts must reject additional providers.");
        Assert(registry.UnregisterOwner("owner-a") == 1, "Owner revocation should report the removed API count.");
        Assert(!registry.TryResolve<ISingleProviderApi>(out _), "Owner revocation must remove the provider from resolution.");
    }
    private static void AssertStructuredExtensionLoggerPreservesContext()
    {
        HostLogEntry captured = null;
        ExtensionHostContext host = new ExtensionHostContext { StructuredLog = entry => captured = entry };
        Exception failure = new InvalidOperationException("test failure");
        host.GetExtensionLogger("tests.logger").Log("operation failed", LogLevel.ERROR, failure, "correlation-1");
        Assert(captured != null && captured.ExtensionId == "tests.logger", "Structured logger must bind the extension ID.");
        Assert(ReferenceEquals(captured.Exception, failure), "Structured logger must preserve the original exception.");
        Assert(captured.CorrelationId == "correlation-1", "Structured logger must preserve the correlation ID.");
    }
    [ExtensionApiContract(AllowMultipleProviders = false)]
    private interface ISingleProviderApi { }

    private sealed class SingleProviderApi : ISingleProviderApi { }

    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
