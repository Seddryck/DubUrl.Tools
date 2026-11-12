using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using static DubUrl.Cli.ConnectionUrls.ConnectionUrlCatalog;

namespace DubUrl.Cli.ConnectionUrls;

internal static class ConnectionNewCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("new", "Build a connection URL in an interactive wizard.");
        command.SetAction((_, cancellationToken) => RunAsync(services, cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var console = services.GetRequiredService<ICliConsole>();
        if (!console.IsInteractive || console.IsInputRedirected)
        {
            console.Error.WriteLine("'duburl connection new' requires an interactive terminal.");
            return CliExitCodes.InvalidInput;
        }

        ConnectionUrlCatalog catalog;
        try { catalog = ConnectionUrlCatalog.Load(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            console.Error.WriteLine("The bundled connection URL catalog could not be read.");
            return CliExitCodes.UnexpectedError;
        }

        var terminal = services.GetService<IAnsiConsole>() ?? AnsiConsole.Console;
        console.Out.WriteLine("Build a connection URL");
        console.Out.WriteLine("Type to search menus. Use Up/Down and Enter to select.");
        console.Out.WriteLine();

        var database = await SelectAsync("Database engine", catalog.Databases.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase),
            item => item.Label, terminal, cancellationToken);
        var route = await SelectAsync("Driver", database.Routes, item => $"{item.Driver} ({item.Api})", terminal, cancellationToken);
        if (route.Note is { } routeNote)
            console.Out.WriteLine(routeNote);
        var location = await SelectAsync("Endpoint", route.Locations,
            id => catalog.Locations[id].Label ?? catalog.Locations[id].Transport, terminal, cancellationToken);

        var methods = catalog.GetAuthentication(route, location);
        // Location fields may be refined by the chosen authentication method.
        // Keep endpoint prompts first when all choices agree on those fields.
        var answers = new Dictionary<string, string>();
        if (methods.Length > 0 && methods.All(method => !method.Fields.Keys.Intersect(catalog.Locations[location].Fields.Keys).Any()))
        {
            var endpointForm = catalog.CreateForm(route, location, methods[0]);
            foreach (var id in endpointForm.ConnectionFields)
                answers[id] = await PromptAsync(endpointForm, id, terminal, cancellationToken);
        }

        var auth = await SelectAsync("Authentication", methods, item => item.Label, terminal, cancellationToken);
        var form = catalog.CreateForm(route, location, auth);
        foreach (var id in form.ConnectionFields.Concat(form.AuthenticationFields).Distinct().Where(id => !answers.ContainsKey(id)))
            answers[id] = await PromptAsync(form, id, terminal, cancellationToken);
        var tls = await SelectAsync("TLS", catalog.GetTlsModes(route, auth), item => item.Label, terminal, cancellationToken);
        foreach (var id in form.AdvancedFields.Where(id => !answers.ContainsKey(id)))
            answers[id] = await PromptAsync(form, id, terminal, cancellationToken);

        var url = await FinishAsync(form, tls, answers, terminal, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        console.Out.WriteLine();
        console.Out.WriteLine("Connection URL");
        console.Out.WriteLine(url);
        var hint = auth.Note ?? (auth.Via == "runtime" ? RuntimeHint(auth) : null);
        if (hint is not null)
        {
            console.Out.WriteLine();
            console.Out.WriteLine(hint);
        }
        return CliExitCodes.Success;
    }

    private static string RuntimeHint(AuthMethod auth)
        => auth.Cli.FirstOrDefault() switch
        {
            "--password-prompt" => "Supply the password when you connect.",
            "--os-identity" => "Use your operating system identity when you connect.",
            "--client-certificate" => "Supply the client certificate and its key when you connect.",
            "--oauth" => "Sign in with OAuth when you connect.",
            _ => "Supply the required credentials when you connect.",
        };

    private static Task<T> SelectAsync<T>(string title, IEnumerable<T> items, Func<T, string> label,
        IAnsiConsole terminal, CancellationToken cancellationToken) where T : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        var choices = items.ToArray();
        if (choices.Length == 1)
            return Task.FromResult(choices[0]);
        return new SelectionPrompt<T>().Title(title).PageSize(10).EnableSearch()
            .SearchPlaceholderText("Type to search").UseConverter(item => Markup.Escape(label(item)))
            .AddChoices(choices).ShowAsync(terminal, cancellationToken);
    }

    private static Task<string> PromptAsync(ConnectionUrlForm form, string id, IAnsiConsole terminal, CancellationToken cancellationToken)
    {
        var field = form.Fields[id];
        var hint = field.DefaultText is { Length: > 0 } value ? $" [{value}]" : field.Required ? "" : " (optional)";
        return new TextPrompt<string>(Markup.Escape(field.Label + hint + ":")).AllowEmpty()
            .Validate(input =>
            {
                try { form.ValidateField(id, input); return ValidationResult.Success(); }
                catch (ConnectionUrlInputException error) { return ValidationResult.Error(Markup.Escape(error.Message)); }
            }).ShowAsync(terminal, cancellationToken);
    }

    private static async Task<string> FinishAsync(ConnectionUrlForm form, TlsMode tls, Dictionary<string, string> answers,
        IAnsiConsole terminal, CancellationToken cancellationToken)
    {
        while (true)
        {
            try { return form.Build(tls, answers); }
            catch (ConnectionUrlInputException error)
            {
                terminal.MarkupLine(Markup.Escape(error.Message));
                answers[error.FieldId] = await PromptAsync(form, error.FieldId, terminal, cancellationToken);
            }
        }
    }
}
