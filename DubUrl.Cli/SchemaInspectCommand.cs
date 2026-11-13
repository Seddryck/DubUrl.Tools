using System.CommandLine;
using DubUrl.Application;
using DubUrl.Discovery;
using DubUrl.Mapping;
using DubUrl.Parsing;
using DubUrl.Schema.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace DubUrl.Cli;

internal static partial class SchemaCommand
{
    private static Command CreateInspect(IServiceProvider services, CancellationToken cancellationToken)
    {
        var url = new Argument<string?>("url") { Arity = ArgumentArity.ZeroOrOne, Description = "Connection URL. Specify exactly one of <url>, --connection or --connection-env." };
        var connection = new Option<string?>("--connection") { Description = "Named connection from configuration." };
        var environment = new Option<string?>("--connection-env") { Description = "Environment variable containing a connection URL." };
        var tables = new Option<string[]>("--table")
        {
            Description = "Qualified table name; repeat to select several. Omit to inspect all visible tables.",
            AllowMultipleArgumentsPerToken = false
        };
        var schemas = new Option<string[]>("--schema") { Description = "Schema name; repeat to select several schemas.", AllowMultipleArgumentsPerToken = false };
        var output = new Option<string?>("--output") { Description = "Write the document to a file. Omit for standard output." };
        var format = new Option<string?>("--output-format") { Description = "yaml or json. Overrides the output filename extension; defaults to YAML on stdout. Unknown extensions require this option." };
        var partial = new Option<bool>("--allow-partial") { Description = "Deliberately export incomplete metadata with diagnostics on standard error. Strict export is the default." };
        var references = new Option<bool>("--include-references") { Description = "Include the transitive foreign-key target tables outside the filters." };
        var basic = new Option<bool>("--basic") { Description = "Request tables, columns, primary keys, and foreign keys only." };
        var readOnly = ConnectionCommand.ReadOnlyOption();
        var command = new Command("inspect", "Read database metadata and export a versioned schema document. Does not change the database.");
        var authentication = new CliAuthentication();
        authentication.AddTo(command);
        command.Arguments.Add(url);
        foreach (var option in new Option[] { connection, environment, tables, schemas, output, format, partial, references, basic, readOnly }) command.Options.Add(option);
        command.SetAction((result, actionToken) => ExecuteSafelyAsync(services, "inspect", async () =>
        {
            using var authenticationLifetime = authentication;
            actionToken.ThrowIfCancellationRequested();
            var directUrl = result.GetValue(url);
            var name = result.GetValue(connection);
            var variable = result.GetValue(environment);
            if (new[] { directUrl, name, variable }.Count(value => value is not null) != 1)
                throw new CliInputException("Specify exactly one of <url>, --connection or --connection-env.");
            if (name?.Contains("://", StringComparison.Ordinal) == true)
                throw new CliInputException("--connection expects a configured name. Pass a URL as the positional argument to 'duburl schema inspect <url>'.");
            var path = result.GetValue(output);
            var outputFormat = InspectOutputFormat(result.GetValue(format), path);
            var selected = InspectTables(result.GetValue(tables) ?? []);
            var options = new DiscoveryOptions
            {
                Tables = selected,
                Schemas = result.GetValue(schemas) ?? [],
                AllowPartial = result.GetValue(partial),
                IncludeReferencedTables = result.GetValue(references),
            };
            if (result.GetValue(basic))
                options = options with { Features = [SchemaFeature.Tables, SchemaFeature.Columns, SchemaFeature.PrimaryKeys, SchemaFeature.ForeignKeys] };
            var value = directUrl ?? ResolveConnection(services, name, variable);
            if (!ConnectionUri.TryParse(value, out _))
                throw new CliInputException("The selected connection URL is invalid.");
            var target = authentication.Configure(services, result, value);
            if (result.GetValue(readOnly))
            {
                target = target.WithReadOnly();
                try { _ = target.Parse(); }
                catch (NotSupportedException) { throw new CliInputException("Native read-only file access is unsupported for this provider or database mode; use a verified SQLite or DuckDB file route."); }
                catch (Exception exception) when (exception is ArgumentException or FormatException or DubUrlException)
                { throw new CliInputException("The selected read-only connection configuration could not be resolved."); }
            }
            var operation = await services.GetRequiredService<ISchemaInspectionService>().InspectAsync(
                target, options, actionToken).ConfigureAwait(false);
            var console = services.GetRequiredService<ICliConsole>();
            foreach (var diagnostic in operation.Diagnostics)
                console.Error.WriteLine(FormatInspectionDiagnostic(diagnostic));
            if (!operation.IsSuccess) return InspectionFailureExitCode(operation);
            WriteInspection(services, operation, path, outputFormat, actionToken);
            return operation.Inspection.IsComplete ? CliExitCodes.Success : CliExitCodes.InspectionIncomplete;
        }, result.GetValue(format) == "json"));
        return command;
    }

    private static string FormatInspectionDiagnostic(DiscoveryDiagnostic diagnostic)
    {
        if (diagnostic.Code != "SDI009")
            return $"{diagnostic.Code}: {diagnostic.Message}";

        var location = diagnostic.Object is { } table
            ? string.Join(".", new[] { table.Catalog, table.Database, table.Schema, table.Name }
                .OfType<string>().Select(QuoteDiagnosticIdentifier)) + " — "
            : "";
        return $"{diagnostic.Code}: {SingleLine(location + diagnostic.Message)}";

        static string QuoteDiagnosticIdentifier(string name)
            => name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
                ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        static string SingleLine(string text)
            => string.Concat(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is
                System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                ? $"\\u{(int)c:x4}" : c.ToString()));
    }

    private static SchemaDocumentFormat InspectOutputFormat(string? format, string? path)
    {
        return format?.ToLowerInvariant() switch
        {
            null => path is null ? SchemaDocumentFormat.Yaml : SchemaDocumentReader.GetFormat(path),
            "yaml" => SchemaDocumentFormat.Yaml,
            "json" => SchemaDocumentFormat.Json,
            _ => throw new CliInputException("--output-format must be 'yaml' or 'json'.")
        };
    }

    private static MetadataName[] InspectTables(string[] tables)
    {
        try { return tables.Select(MetadataNames.Parse).ToArray(); }
        catch (ArgumentException) { throw new CliInputException("A --table value is not a valid qualified identifier."); }
    }

    private static int InspectionFailureExitCode(SchemaExportResult operation)
    {
        if (operation.Diagnostics.Any(diagnostic => diagnostic.Coverage == MetadataCoverage.Unsupported))
            return CliExitCodes.InspectionUnsupported;
        return operation.Inspection.Schema is not null ? CliExitCodes.InspectionIncomplete : CliExitCodes.InspectionFailed;
    }

    private static void WriteInspection(IServiceProvider services, SchemaExportResult operation, string? path,
        SchemaDocumentFormat outputFormat, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        services.GetRequiredService<SchemaDocumentWriter>().Write(memory, operation.Document!, outputFormat);
        if (path is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(path, memory.ToArray());
        }
        else
        {
            memory.Position = 0;
            using var reader = new StreamReader(memory);
            services.GetRequiredService<ICliConsole>().Out.Write(reader.ReadToEnd());
        }
    }
}
