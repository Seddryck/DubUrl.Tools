using DubUrl.Cli.ConnectionUrls;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Spectre.Console;
using Spectre.Console.Testing;

namespace DubUrl.Cli.Testing;

public class ConnectionUrlWizardTests
{
    private static readonly ConnectionUrlCatalog Catalog = ConnectionUrlCatalog.Load();

    [Test]
    public async Task SearchAndArrowKeysBuildTheMySqlExampleAfterRetryingAnInvalidPort()
    {
        using var terminal = new TestConsole().Interactive();
        terminal.Input.PushTextWithEnter("mysql");
        terminal.Input.PushKey(ConsoleKey.DownArrow);
        terminal.Input.PushKey(ConsoleKey.UpArrow);
        terminal.Input.PushKey(ConsoleKey.Enter);
        terminal.Input.PushTextWithEnter("db.example.com");
        terminal.Input.PushTextWithEnter("70000");
        terminal.Input.PushKey(ConsoleKey.Enter);
        terminal.Input.PushTextWithEnter("sales");
        terminal.Input.PushKey(ConsoleKey.Enter);
        terminal.Input.PushTextWithEnter("alice");
        terminal.Input.PushKey(ConsoleKey.DownArrow);
        terminal.Input.PushKey(ConsoleKey.Enter);

        var console = new ConsoleCapture(interactive: true, redirected: false);
        using var services = new ServiceCollection().AddSingleton<ICliConsole>(console)
            .AddSingleton<IAnsiConsole>(terminal).BuildServiceProvider();

        var exitCode = await CliHost.InvokeAsync(["connection", "new"], services);

        Assert.That(exitCode, Is.EqualTo(CliExitCodes.Success), console.Error.ToString());
        Assert.That(terminal.Output, Does.Contain("Enter a port from 1 to 65535."));
        Assert.That(terminal.Output, Does.Contain("Port [3306]:"));
        Assert.That(terminal.Output, Does.Not.Contain("Endpoint"));
        var lines = console.Out.ToString()!.Split(Environment.NewLine);
        Assert.That(lines, Has.Member("mysql+tls://alice@db.example.com/sales"));
        Assert.That(lines, Has.Member("Supply the password when you connect."));
    }

    [TestCase(false, true)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task RedirectedTerminalFailsWithoutResolvingProviderServices(bool interactive, bool redirected)
    {
        var console = new ConsoleCapture(interactive, redirected);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProviderPacks:StorePath"] = "\0invalid",
        }).Build();
        using var services = CliComposition.CreateForCommand(["connection", "new"], console, configuration);

        var exitCode = await CliHost.InvokeAsync(["connection", "new"], services);

        Assert.That(services.GetService<IConfiguration>(), Is.Null);
        Assert.That(exitCode, Is.EqualTo(CliExitCodes.InvalidInput));
        Assert.That(console.Out.ToString(), Is.Empty);
        Assert.That(console.Error.ToString(), Does.Contain("requires an interactive terminal"));
    }

    [Test]
    public async Task CancellationReportsNoConnectionUrl()
    {
        var console = new ConsoleCapture(interactive: true, redirected: false);
        using var services = CliComposition.CreateForCommand(["connection", "new"], console);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await CliHost.InvokeAsync(["connection", "new"], services, cancellation.Token);

        Assert.That(exitCode, Is.EqualTo(CliExitCodes.Cancelled));
        Assert.That(console.Out.ToString(), Does.Not.Contain("Connection URL"));
        Assert.That(console.Error.ToString(), Does.Contain("Operation cancelled."));
    }

    [Test]
    public void EveryPublishedEndpointAndAuthenticationChoiceResolvesFieldsAndTls()
    {
        foreach (var route in Catalog.Databases.SelectMany(database => database.Routes))
            foreach (var endpoint in route.Locations)
            {
                var methods = Catalog.GetAuthentication(route, endpoint);
                Assert.That(methods, Is.Not.Empty, $"{route.Id}/{endpoint}");
                foreach (var auth in methods)
                {
                    Assert.That(Catalog.GetTlsModes(route, auth), Is.Not.Empty, $"{route.Id}/{endpoint}/{auth.Id}");
                    Assert.That(Catalog.CreateForm(route, endpoint, auth).Fields.Values.Select(field => field.Label),
                        Has.All.Not.Empty, $"{route.Id}/{endpoint}/{auth.Id}");
                }
            }
    }

    [Test]
    public void TrinoPasswordOnlyOffersSecureTlsModes()
    {
        var route = FindRoute("nreco");
        var auth = Catalog.GetAuthentication(route, route.Locations[0]).Single(item => item.Id == "password");

        Assert.That(Catalog.GetTlsModes(route, auth).Select(item => item.Id), Is.EqualTo(new[] { "verified" }));
    }

    [TestCase("1.2.3.256")]
    [TestCase("nonip.123")]
    [TestCase("[::1]:443#[x]")]
    public void RejectsInvalidHostsLikeTheWebBuilder(string host)
    {
        var route = FindRoute("npgsql");
        var endpoint = route.Locations[0];
        var auth = Catalog.GetAuthentication(route, endpoint).First();
        var form = Catalog.CreateForm(route, endpoint, auth);

        Assert.That(() => form.ValidateField("host", host), Throws.TypeOf<ConnectionUrlInputException>()
            .With.Message.EqualTo("Enter a valid hostname or IP address."));
    }

    // These expected URLs are also covered by DubUrl's web builder tests.
    [TestCase("npgsql", null, "password", "verified", "host=[::1];database=analytics/data;username=qa+analyst;port=05432;searchPath=team & reporting",
        "postgres://qa%2Banalyst@[::1]/analytics%2Fdata?SSL%20Mode=VerifyFull&GSS%20Encryption%20Mode=Disable&Search%20Path=team%20%26%20reporting")]
    [TestCase("npgsql", null, "password", "default", "host=Bücher.example;database=sales;username=qa",
        "postgres://qa@xn--bcher-kva.example/sales")]
    [TestCase("npgsql", null, "password", "default", "host=0177.0.0.1;database=sales;username=qa",
        "postgres://qa@127.0.0.1/sales")]
    [TestCase("npgsql", null, "password", "default", "host=::ffff:192.0.2.1;database=sales;username=qa",
        "postgres://qa@[::ffff:c000:201]/sales")]
    [TestCase("sqlclient", null, "password", "default", "host=sql.example;database=reporting;username=analyst;instance=reporting;port=1433",
        "mssql://analyst@sql.example:1433/reporting/reporting")]
    [TestCase("mysql-odbc", null, "password", "verified", "host=db.example;database=db;username=qa;port=03308",
        "odbc+mysql://qa@db.example/db?SSLMODE=VERIFY_IDENTITY&PORT=3308")]
    [TestCase("msodbc18", null, "password", "default", "host=db.example;database=db;username=qa;port=1433",
        "odbc+mssql://qa@db.example/db?Driver=ODBC%20Driver%2018%20for%20SQL%20Server")]
    [TestCase("spark-odbc", "thriftHttp", "driver", "on", "host=spark.example;httpPath=/gateway/spark;schema=analytics",
        "odbc+spark+https://spark.example/gateway/spark/schema/analytics")]
    [TestCase("hive-odbc", "thriftTcp", "thriftPassword", "on", "host=spark.example;schema=analytics;username=qa",
        "odbc+hive+tcps://qa@spark.example/schema/analytics?AuthMech=3")]
    [TestCase("databricks-odbc", "warehouse", "databricksPat", "default", "host=workspace.example;warehouseId=abc123;catalog=main;schema=reporting",
        "odbc+databricks://token@workspace.example/warehouse/abc123/main/reporting?AuthMech=3")]
    [TestCase("databricks-odbc", "cluster", "databricksOAuthM2M", "default", "host=workspace.example;workspaceId=123;clusterId=example-012",
        "odbc+databricks://workspace.example/cluster/123/example-012?AuthMech=11&Auth_Flow=1")]
    [TestCase("pbi-xmla-adomd", null, null, "default", "workspace=V1.0", "powerbi:///v1.0/myorg/V1.0")]
    [TestCase("pbi-desktop-adomd", "powerBiPort", null, "default", "port=51234", "pbidesktop://localhost:51234")]
    [TestCase("ssas-multidim-adomd", null, null, "default", "host=ssas.example;catalog=analytics;cube=sales", "ssasmultidim://ssas.example/~/analytics/sales")]
    [TestCase("sqlite-native", null, null, "default", @"file=C:\Data\example file.db", "sqlite:///C:/Data/example%20file.db")]
    [TestCase("sqlite-native", null, null, "default", "file=/var/data/example.db", "sqlite:////var/data/example.db")]
    [TestCase("duckdb-adbc", "memory", null, "default", "", "adbc+duckdb://")]
    [TestCase("excel-ace", null, null, "default", "file=Reports.xlsm", "oledb+xlsm:///Reports.xlsm")]
    public void MatchesWebBuilder(string routeId, string? endpoint, string? authId, string tlsId, string inputs, string expected)
    {
        var route = FindRoute(routeId);
        endpoint ??= route.Locations[0];
        var auth = Catalog.GetAuthentication(route, endpoint).First(item => authId is null || item.Id == authId);
        var tls = Catalog.GetTlsModes(route, auth).Single(item => item.Id == tlsId);
        var answers = inputs.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(input => input.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);

        Assert.That(Catalog.CreateForm(route, endpoint, auth).Build(tls, answers), Is.EqualTo(expected));
    }

    private static ConnectionUrlCatalog.Route FindRoute(string id)
        => Catalog.Databases.SelectMany(database => database.Routes).Single(route => route.Id == id);

    private sealed class ConsoleCapture(bool interactive, bool redirected) : ICliConsole
    {
        public TextReader In => TextReader.Null;
        public TextWriter Out { get; } = new StringWriter();
        public TextWriter Error { get; } = new StringWriter();
        public bool IsInputRedirected => redirected;
        public bool IsInteractive => interactive;
    }
}
