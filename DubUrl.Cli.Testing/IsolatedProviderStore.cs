using NUnit.Framework;

namespace DubUrl.Cli.Testing;

// Default composition must never inspect or modify a developer's installed connectors.
[SetUpFixture]
public sealed class IsolatedProviderStore
{
    private string? previousStore;
    private string? previousConfiguration;
    private string path = null!;

    [OneTimeSetUp]
    public void UseTestStore()
    {
        previousStore = Environment.GetEnvironmentVariable("DUBURL_PROVIDER_STORE");
        previousConfiguration = Environment.GetEnvironmentVariable("DUBURL_ProviderPacks__StorePath");
        path = Path.Combine(Path.GetTempPath(), "duburl-cli-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DUBURL_PROVIDER_STORE", path);
        Environment.SetEnvironmentVariable("DUBURL_ProviderPacks__StorePath", path);
    }

    [OneTimeTearDown]
    public void RestoreStore()
    {
        Environment.SetEnvironmentVariable("DUBURL_PROVIDER_STORE", previousStore);
        Environment.SetEnvironmentVariable("DUBURL_ProviderPacks__StorePath", previousConfiguration);
        if (!Directory.Exists(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("duburl-cli-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test provider store path.");
        Directory.Delete(fullPath, recursive: true);
    }
}
