using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DubUrl.Cli.ConnectionUrls;

internal sealed class ConnectionUrlCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public required int SchemaVersion { get; init; }
    public required Database[] Databases { get; init; }
    public required Dictionary<string, Location> Locations { get; init; }
    public required Dictionary<string, JsonObject> Layouts { get; init; }
    public required Dictionary<string, JsonObject> Authentication { get; init; }
    public required Dictionary<string, Dictionary<string, JsonObject>> DriverFields { get; init; }
    public required Dictionary<string, TlsPolicy> TlsPolicies { get; init; }

    public static ConnectionUrlCatalog Load()
    {
        using var bundle = typeof(ConnectionUrlCatalog).Assembly.GetManifestResourceStream("DubUrl.Cli.ConnectionUrlCatalog.zip")
            ?? throw new InvalidDataException("The bundled connection URL catalog is missing.");
        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read);
        using var json = (zip.GetEntry("catalog.json") ?? throw new InvalidDataException("The bundle is missing catalog.json.")).Open();
        var catalog = JsonSerializer.Deserialize<ConnectionUrlCatalog>(json, JsonOptions);
        return catalog is { SchemaVersion: 2, Databases.Length: > 0 } ? catalog
            : throw new InvalidDataException("The bundled connection URL catalog uses an unsupported schema.");
    }

    public AuthMethod[] GetAuthentication(Route route, string locationId)
        => route.Auth.Select(item =>
        {
            var overrides = item as JsonObject;
            var id = overrides is null ? item.GetValue<string>() : overrides["id"]!.GetValue<string>();
            return Merge<AuthMethod>(Authentication[id], overrides) with { Id = id };
        }).Where(method => method.Locations is null || method.Locations.Contains(locationId)).ToArray();

    public TlsMode[] GetTlsModes(Route route, AuthMethod auth)
    {
        var policy = TlsPolicies[auth.TlsOverride ?? route.Tls ?? "driverDefault"];
        // Spectre starts on the first item, which should be the catalog's suggested mode.
        return policy.Modes.Where(mode => !auth.RequiresTls || mode.Secure)
            .OrderByDescending(mode => mode.Id == policy.Default).ToArray();
    }

    public ConnectionUrlForm CreateForm(Route route, string locationId, AuthMethod auth)
    {
        var location = Locations[locationId];
        var driverFields = DriverFields.GetValueOrDefault(route.Api) ?? [];
        var fixedOptions = route.Query.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var advancedFields = driverFields.Where(pair => !fixedOptions.Contains(pair.Value["queryKey"]!.GetValue<string>()))
            .Select(pair => pair.Key).ToArray();
        var fields = new Dictionary<string, Field>();
        foreach (var id in location.Fields.Keys.Concat(advancedFields).Concat(auth.Fields.Keys).Distinct())
        {
            var definition = Overlay(location.Fields.GetValueOrDefault(id), driverFields.GetValueOrDefault(id),
                route.Fields.GetValueOrDefault(id), auth.Fields.GetValueOrDefault(id));
            fields.Add(id, Merge<Field>(Layouts[definition["layout"]!.GetValue<string>()], definition));
        }
        return new(route, locationId, location, auth, fields, advancedFields);
    }

    private static T Merge<T>(params JsonObject?[] sources)
        => Overlay(sources).Deserialize<T>(JsonOptions) ?? throw new InvalidDataException("A catalog definition is missing.");

    private static JsonObject Overlay(params JsonObject?[] sources)
    {
        var merged = new JsonObject();
        foreach (var source in sources.OfType<JsonObject>())
            foreach (var (key, value) in source)
                merged[key] = value?.DeepClone();
        return merged;
    }

    internal sealed record Database(string Id, string Label, Route[] Routes);

    internal sealed record Route(string Id, string Api, string Driver, string Scheme, string[] Locations, JsonNode[] Auth)
    {
        public string? Tls { get; init; }
        public string? Note { get; init; }
        public Dictionary<string, JsonObject> Fields { get; init; } = [];
        public Dictionary<string, string> Query { get; init; } = [];
        public Dictionary<string, string>? SchemeByExtension { get; init; }
        public Dictionary<string, Dictionary<string, string>> SchemeByLocation { get; init; } = [];
    }

    internal sealed record Location
    {
        public string? Label { get; init; }
        public required string Transport { get; init; }
        public string? FixedHost { get; init; }
        public Dictionary<string, JsonObject> Fields { get; init; } = [];
        public Dictionary<string, string> Requires { get; init; } = [];
        public string[][] Exclusive { get; init; } = [];
        public JsonNode[] Path { get; init; } = [];
    }

    internal sealed record Field
    {
        public required string Label { get; init; }
        public bool Required { get; init; }
        public string? Url { get; init; }
        public string? QueryKey { get; init; }
        public string? Pattern { get; init; }
        public JsonNode? Default { get; init; }
        public string[] DefaultUnless { get; init; } = [];
        public string DefaultText => Default?.ToString().Trim() ?? "";
    }

    internal sealed record AuthMethod(string Label, string Via)
    {
        public string Id { get; init; } = "";
        public string? FixedUser { get; init; }
        public string? Note { get; init; }
        public string[]? Locations { get; init; }
        public string[] Cli { get; init; } = [];
        public bool RequiresTls { get; init; }
        public string? TlsOverride { get; init; }
        public Dictionary<string, JsonObject> Fields { get; init; } = [];
        public Dictionary<string, string> Query { get; init; } = [];
        public Dictionary<string, Dictionary<string, string>> QueryByLocation { get; init; } = [];
    }

    internal sealed record TlsPolicy(string? Default, TlsMode[] Modes);

    internal sealed record TlsMode(string Id, string Label)
    {
        public bool Secure { get; init; }
        public string? SchemeSuffix { get; init; }
        public Dictionary<string, string> Query { get; init; } = [];
    }
}
