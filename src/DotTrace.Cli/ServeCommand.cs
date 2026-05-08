using System.CommandLine;

internal static partial class ProgramEntry
{
    private static Command CreateServeCommand()
    {
        var dbOption = CreateDbOption();
        var snapshotOption = new Option<long?>("--snapshot")
        {
            Description = "Use a specific snapshot id as the initial browser selection."
        };
        snapshotOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<long?>();
            if (value is <= 0)
            {
                result.AddError("--snapshot must be a positive integer.");
            }
        });

        var portOption = new Option<int?>("--port")
        {
            Description = "Bind the local browser UI to a fixed localhost port. Defaults to an ephemeral port."
        };
        portOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value is <= 0 or > 65535)
            {
                result.AddError("--port must be between 1 and 65535.");
            }
        });

        var maxDepthOption = new Option<int?>("--max-depth")
        {
            Description = "Initial recursive tree expansion depth."
        };
        maxDepthOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value is <= 0)
            {
                result.AddError("--max-depth must be a positive integer.");
            }
        });

        var serveCommand = new Command("serve", "Host a local read-only browser UI for a SQLite call-graph cache.");
        serveCommand.Options.Add(dbOption);
        serveCommand.Options.Add(snapshotOption);
        serveCommand.Options.Add(portOption);
        serveCommand.Options.Add(maxDepthOption);
        serveCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new BrowserServerOptions(
                parseResult.GetValue(dbOption)!,
                parseResult.GetValue(snapshotOption),
                parseResult.GetValue(portOption),
                parseResult.GetValue(maxDepthOption));

            return await BrowserServer.RunAsync(options, Console.Error, cancellationToken);
        });

        return serveCommand;
    }
}
