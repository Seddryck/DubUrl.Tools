using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static DubUrl.Cli.ConnectionUrls.ConnectionUrlCatalog;

namespace DubUrl.Cli.ConnectionUrls;

internal sealed class ConnectionUrlForm(Route route, string locationId, Location location, AuthMethod auth,
    Dictionary<string, Field> fields, string[] advancedFields)
{
    public IReadOnlyDictionary<string, Field> Fields => fields;
    public IEnumerable<string> ConnectionFields => location.Fields.Keys;
    public IEnumerable<string> AuthenticationFields => auth.Fields.Keys;
    public IEnumerable<string> AdvancedFields => advancedFields;

    public void ValidateField(string id, string input)
    {
        var field = fields[id];
        var value = ValueOrDefault(input, field);
        var label = field.Label.ToLowerInvariant();
        if (value.Length == 0 && field.Required)
            throw new ConnectionUrlInputException(id, $"Enter {label}.");
        if (value.Length > 0 && field.Pattern is { } pattern &&
            !Regex.IsMatch(value, $@"\A(?:{pattern})\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new ConnectionUrlInputException(id, $"Check {label}.");

        if (field.Url == "host")
            EncodeHost(id, value);
        if (field.Url == "port" && value.Length > 0)
            ParsePort(id, value);
        foreach (var part in location.Path.Where(part => PathField(part) == id))
            EncodePathPart(part, key => key == id ? value : "");
        if (id == "file" && route.SchemeByExtension is not null)
            ResolveFileScheme(value);
    }

    public string Build(TlsMode tls, IReadOnlyDictionary<string, string> answers)
    {
        string Entered(string id) => fields.ContainsKey(id) ? answers.GetValueOrDefault(id, "").Trim() : "";
        string Value(string? id) => id is not null && fields.TryGetValue(id, out var field) ? ValueOrDefault(Entered(id), field) : "";

        foreach (var id in fields.Keys)
            ValidateField(id, Entered(id));
        foreach (var (id, dependency) in location.Requires)
            if (Value(id).Length > 0 && Value(dependency).Length == 0)
                throw new ConnectionUrlInputException(dependency, $"{fields[id].Label} requires {fields[dependency].Label.ToLowerInvariant()}.");
        foreach (var pair in location.Exclusive)
        {
            var (left, right) = (pair[0], pair[1]);
            if (Entered(left).Length > 0 && Entered(right).Length > 0)
                throw new ConnectionUrlInputException(right, $"Choose either {fields[left].Label.ToLowerInvariant()} or {fields[right].Label.ToLowerInvariant()}.");
        }

        var portField = FindRole("port");
        var port = "";
        if (portField is not null && Entered(portField).Length > 0)
        {
            var explicitPort = ParsePort(portField, Entered(portField)).ToString(CultureInfo.InvariantCulture);
            var definition = fields[portField];
            if (explicitPort != definition.DefaultText || definition.DefaultUnless.Any(id => Entered(id).Length > 0))
                port = explicitPort;
        }

        var path = location.Path.SelectMany(part => EncodePathPart(part, Value)).ToArray();
        var scheme = ResolveScheme(tls, Value);
        var user = auth.FixedUser ?? Value(FindRole("username"));
        var hostField = FindRole("host");
        var host = EncodeHost(hostField ?? "host", location.FixedHost ?? Value(hostField));
        var options = EncodeOptions(tls, id => id == portField ? port : Value(id));
        return scheme + "://" + (user.Length > 0 ? Escape(user) + "@" : "") + host
            + (port.Length > 0 && fields[portField!].QueryKey is null ? ":" + port : "")
            + (path.Length > 0 ? "/" + string.Join('/', path) : "")
            + options;
    }

    private string? FindRole(string role) => fields.FirstOrDefault(pair => pair.Value.Url == role).Key;
    private static string ValueOrDefault(string input, Field field) => input.Trim() is { Length: > 0 } value ? value : field.DefaultText;
    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static int ParsePort(string id, string value)
    {
        if (value.Length <= 5 && value.All(char.IsAsciiDigit)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
            return port;
        throw new ConnectionUrlInputException(id, "Enter a port from 1 to 65535.");
    }

    private static string EncodeHost(string id, string value)
    {
        if (value.Length == 0)
            return "";
        if (value.Contains(':'))
        {
            var address = value.StartsWith('[') && value.EndsWith(']') ? value[1..^1] : value;
            if (!address.Contains('%') && !address.Contains('[') && !address.Contains(']')
                && IPAddress.TryParse(address, out var ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6)
                return "[" + FormatIpv6(ipv6) + "]";
        }
        else if (!value.Any(IsForbiddenHostCharacter) && EncodeDomain(value) is { } domain)
        {
            return domain;
        }
        throw new ConnectionUrlInputException(id, "Enter a valid hostname or IP address.");
    }

    private static bool IsForbiddenHostCharacter(char character)
        => char.IsWhiteSpace(character) || character < 0x20 || character is '\u007f' or '\ufeff'
            || "#/:<>?@%\\[]^|".Contains(character);

    private static string? EncodeDomain(string value)
    {
        var idn = new IdnMapping();
        var parts = value.Split(['.', '\u3002', '\uff0e', '\uff61']);
        try
        {
            for (var i = 0; i < parts.Length; i++)
            {
                var label = parts[i];
                if (label.Any(character => !char.IsAscii(character)))
                    label = idn.GetAscii(label);
                if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
                    idn.GetUnicode(label); // Validate a user-supplied Punycode label too.
                parts[i] = label.ToLowerInvariant();
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        var domain = string.Join('.', parts);
        if (domain.Length == 0 || domain.Any(IsForbiddenHostCharacter))
            return null;

        // Like the web builder's URL parser, a numeric final label makes this an IPv4 candidate.
        var addressParts = parts.Length > 1 && parts[^1].Length == 0 ? parts[..^1] : parts;
        var last = addressParts[^1];
        if (last.Length == 0 || !(last.All(char.IsAsciiDigit)
            || last.StartsWith("0x", StringComparison.Ordinal) && last.Skip(2).All(char.IsAsciiHexDigit)))
            return domain;

        var ipv4Text = string.Join('.', addressParts.Select(part => part == "0x" ? "0" : part));
        return IPAddress.TryParse(ipv4Text, out var address) && address.AddressFamily == AddressFamily.InterNetwork
            ? address.ToString()
            : null;
    }

    private static string FormatIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var groups = new ushort[8];
        var zeroStart = 0;
        var longestStart = 0;
        var longestLength = 1;
        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2));
            if (groups[i] != 0)
                zeroStart = i + 1;
            else if (i - zeroStart + 1 > longestLength)
            {
                longestStart = zeroStart;
                longestLength = i - zeroStart + 1;
            }
        }
        var hex = groups.Select(group => group.ToString("x", CultureInfo.InvariantCulture)).ToArray();
        return longestLength == 1 ? string.Join(':', hex)
            : string.Join(':', hex[..longestStart]) + "::" + string.Join(':', hex[(longestStart + longestLength)..]);
    }

    private static string? PathField(JsonNode part) => part is JsonValue ? part.GetValue<string>()
        : Text(part, "field") ?? Text(part, "file") ?? Text(part, "relativeFile") ?? Text(part, "split");

    private static string? Text(JsonNode node, string property) => node[property]?.GetValue<string>();

    private string[] EncodePathPart(JsonNode part, Func<string?, string> value)
    {
        if (part is JsonValue || Text(part, "field") is not null)
        {
            var id = PathField(part)!;
            var segment = value(id);
            if (segment.Length == 0 && part is JsonObject
                && (part["fallbackWhen"] is not JsonArray when || when.Any(key => value(key!.GetValue<string>()).Length > 0)))
                segment = Text(part, "fallback") ?? "";
            if (segment is "." or "..")
                throw new ConnectionUrlInputException(id, $"Enter a valid {fields[id].Label.ToLowerInvariant()}.");
            return segment.Length == 0 ? [] : [Escape(segment)];
        }
        if (Text(part, "literal") is { } literal)
        {
            var when = Text(part, "when");
            var condition = value(when);
            var expected = Text(part, "equalsIgnoreCase");
            return (when is null || condition.Length > 0) && (expected is null || condition.Equals(expected, StringComparison.OrdinalIgnoreCase))
                ? [Escape(literal)] : [];
        }
        if (Text(part, "file") is { } file)
            return FilePath(file, value(file), relativeOnly: false);
        if (Text(part, "relativeFile") is { } relativeFile)
            return FilePath(relativeFile, value(relativeFile), relativeOnly: true);
        var split = Text(part, "split")!;
        return EndpointPath(split, value(split), fields[split].DefaultText, Text(part, "reserveSuffix"));
    }

    private static string[] SplitPath(string id, string value)
    {
        var parts = value.Trim('/').Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
            throw new ConnectionUrlInputException(id, "Enter a path with valid segments.");
        return parts;
    }

    private static bool HasDriveLetter(string value) => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static string[] FilePath(string id, string value, bool relativeOnly)
    {
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            throw new ConnectionUrlInputException(id, "Enter a local path.");
        if (relativeOnly && (normalized.StartsWith('/') || HasDriveLetter(normalized)))
            throw new ConnectionUrlInputException(id, "Enter a relative path for this ODBC driver.");
        if (HasDriveLetter(normalized) && normalized.Length > 2 && normalized[2] != '/')
            throw new ConnectionUrlInputException(id, "Enter a complete Windows file path.");
        var parts = SplitPath(id, normalized);
        if (parts[0].Length == 2 && HasDriveLetter(parts[0]))
        {
            if (!HasDriveLetter(normalized) || parts.Length < 2)
                throw new ConnectionUrlInputException(id, "Enter a complete Windows file path.");
            return [parts[0], .. parts.Skip(1).Select(Escape)];
        }
        return normalized.StartsWith('/') ? ["", .. parts.Select(Escape)] : parts.Select(Escape).ToArray();
    }

    private static string[] EndpointPath(string id, string value, string defaultPath, string? reserveSuffix)
    {
        if (value.Length == 0)
            return [];
        var parts = SplitPath(id, value);
        if (defaultPath.Length > 0 && parts.SequenceEqual(SplitPath(id, defaultPath)))
            return [];
        if (reserveSuffix is not null && parts.Length >= 2 && parts[^2] == reserveSuffix)
            throw new ConnectionUrlInputException(id, $"Choose an HTTP endpoint that does not end in /{reserveSuffix}/<name>.");
        return parts.Select(Escape).ToArray();
    }

    private string ResolveScheme(TlsMode tls, Func<string?, string> value)
    {
        if (route.SchemeByExtension is not null)
            return ResolveFileScheme(value("file"));
        var schemes = route.SchemeByLocation.GetValueOrDefault(locationId);
        return (schemes?.GetValueOrDefault(tls.Id) ?? schemes?.GetValueOrDefault("default") ?? route.Scheme) + tls.SchemeSuffix;
    }

    private string ResolveFileScheme(string file)
    {
        var schemes = route.SchemeByExtension!;
        var extension = Regex.Match(file, @"\.([A-Za-z0-9]+)\z").Groups[1].Value.ToLowerInvariant();
        if (schemes.TryGetValue(extension, out var scheme))
            return scheme;
        throw new ConnectionUrlInputException("file", $"Choose a file ending in {string.Join(", ", schemes.Keys.Select(key => "." + key))}.");
    }

    private string EncodeOptions(TlsMode tls, Func<string, string> value)
    {
        // Overwrites keep their position while taking the winning key's casing.
        var options = new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { route.Query, tls.Query, auth.Query, auth.QueryByLocation.GetValueOrDefault(locationId) })
            if (source is not null)
                foreach (var option in source)
                    options[option.Key] = option;
        foreach (var (id, field) in fields)
            if (field.QueryKey is { } key && value(id) is { Length: > 0 } setting)
                options[key] = new(key, setting);
        return options.Count == 0 ? "" : "?" + string.Join('&', options.Values.Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value)));
    }
}

internal sealed class ConnectionUrlInputException(string fieldId, string message) : Exception(message)
{
    public string FieldId { get; } = fieldId;
}
