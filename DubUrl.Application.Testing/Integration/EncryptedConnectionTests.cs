using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;
using DubUrl.Application;
using DubUrl.Authentication;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Provider.DuckDb.Discovery;
using DubUrl.Provider.DuckDb.Mapping;
using DubUrl.Provider.DuckDb.Native;
using DubUrl.Schema;
using DuckDB.NET.Data;
using NUnit.Framework;

namespace DubUrl.Provider.DuckDb.QA;

[NonParallelizable, Category("DuckDB"), Category("AdoProvider"), Category("Authentication")]
public class EncryptedConnectionTests
{
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    private string path = null!;
    private string key = null!;
    private ISchemeRegistry registry = null!;
    private DbProviderFactory? previous;
    private bool hadFactory;
    private string Url => "duckdb:///" + path.Replace('\\', '/').Replace("%", "%25").Replace("#", "%23").Replace(" ", "%20");
    private ConnectionUrl Target(bool strategy = false)
        => strategy ? new ConnectionUrl(Url, registry).WithAuthentication(ConnectionAuthentication.FilePassword(key))
            : new ConnectionUrl(Url + "?ENCRYPTION_KEY=" + Uri.EscapeDataString(key), registry);

    [SetUp]
    public void Setup()
    {
        hadFactory = DbProviderFactories.TryGetFactory("DuckDB.NET.Data", out previous);
        DbProviderFactories.RegisterFactory("DuckDB.NET.Data", DuckDBClientFactory.Instance);
        var builder = new ProviderCatalogBuilder();
        builder.AddModule(new AttributeProviderModule(typeof(DuckdbMapper).Assembly));
        registry = builder.Build();
        path = Path.Combine(Path.GetTempPath(), "duburl-encrypted-' %#+-" + Guid.NewGuid().ToString("N") + ".duckdb");
        key = Guid.NewGuid().ToString("N") + "%2B +&'\";--";
        using var setup = new DuckDBConnection("DataSource=:memory:");
        setup.Open();
        Execute(setup, "ATTACH " + Literal(path) + " AS secured (ENCRYPTION_KEY " + Literal(key) + "); USE secured");
        Execute(setup, "CREATE TABLE sample(id INTEGER PRIMARY KEY, title VARCHAR); INSERT INTO sample VALUES(1,'first'); CREATE INDEX ix_title ON sample(title); CREATE VIEW sample_view AS SELECT id,title FROM sample");
    }

    [TearDown]
    public void Cleanup()
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".wal")) File.Delete(path + ".wal");
        if (hadFactory) DbProviderFactories.RegisterFactory("DuckDB.NET.Data", previous!);
        else DbProviderFactories.UnregisterFactory("DuckDB.NET.Data");
    }

    [Test]
    public async Task MissingFile_IsNotCreatedDuringPreparationOrOpen()
    {
        File.Delete(path);
        var target = Target();
        _ = target.Parse(); target.ValidateAuthentication(); target.ValidateConfiguration();
        using var connection = target.Connect();
        Assert.That(connection.State, Is.EqualTo(ConnectionState.Closed));
        Assert.That(File.Exists(path), Is.False);
        Assert.Throws<FileNotFoundException>(() => connection.Open());
        Assert.That(File.Exists(path), Is.False);
        Assert.That((await new ConnectionCheckService().CheckAsync(target, new())).Success, Is.False);
    }

    [Test]
    public async Task ConnectionCheckAndSchemaDeployment_UsePreparedOpening()
    {
        var result = await new ConnectionCheckService().CheckAsync(Target(), new ConnectionCheckOptions { Table = "sample" });
        Assert.That(result.Success, Is.True);
        var inspected = await new SchemaInspector().InspectAsync(Target());
        Assert.That(inspected.IsSuccess, Is.True);
        new SchemaScriptDeployer().DeploySchema(Target(), "CREATE TABLE deployed(id INTEGER)");
        Assert.That((await new SchemaInspector().InspectAsync(Target())).Schema!.Tables.Values.Any(t => t.Name == "deployed"), Is.True);
    }

    private static void Execute(DuckDBConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Scalar(DuckDBConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
