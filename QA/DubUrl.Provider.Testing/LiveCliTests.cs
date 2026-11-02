using System.Data.Common;
using DubUrl.ProviderTesting;
using Microsoft.Data.Sqlite;
using NUnit.Framework;

namespace DubUrl.Tools.Testing;

[TestFixture, Category("LiveCli")]
public sealed class LiveCliTests : CliConnectionCheckContract
{
    private string? localPath;
    protected override string CliConnectionUrl => Environment.GetEnvironmentVariable("DUBURL_TOOLS_QA_URL")
        ?? "sqlite:///" + localPath!.Replace('\\', '/');
    protected override string CliTableName => Environment.GetEnvironmentVariable("DUBURL_TOOLS_QA_TABLE") ?? "Customer";
    protected override string? CliEmptyTableName => localPath is null ? null : "EmptyCustomer";

    [OneTimeSetUp]
    public void ProvisionLocalFixture()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DUBURL_TOOLS_QA_URL"))) return;
        DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
        localPath = Path.Combine(Path.GetTempPath(), "duburl-tools-qa-" + Guid.NewGuid().ToString("N") + ".db");
        using var connection = new SqliteConnection("Data Source=" + localPath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Customer(id INTEGER); INSERT INTO Customer VALUES(1); CREATE TABLE EmptyCustomer(id INTEGER)";
        command.ExecuteNonQuery();
    }

    [OneTimeTearDown]
    public void RemoveLocalFixture()
    {
        if (localPath is null) return;
        SqliteConnection.ClearAllPools();
        File.Delete(localPath);
    }
}
