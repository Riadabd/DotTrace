using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using System.Text;
using DotTrace.Core.Analysis;
using DotTrace.Core.Persistence;
using DotTrace.Core.Rendering;

Console.OutputEncoding = Encoding.UTF8;

var exitCode = await ProgramEntry.RunAsync(args);
return exitCode;

internal static class ProgramEntry
{
    public static Task<int> RunAsync(string[] args)
    {
        return CreateRootCommand().Parse(NormalizeArgs(args)).InvokeAsync();
    }

    private static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("Roslyn-powered static call-tree explorer for C# solutions and projects.");
        rootCommand.Subcommands.Add(CreateCacheCommand());
        rootCommand.Subcommands.Add(CreateTreeCommand());
        rootCommand.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return rootCommand;
    }

    private static Command CreateCacheCommand()
    {
        var cacheCommand = new Command("cache", "Build and inspect SQLite call-graph snapshots.");
        cacheCommand.Subcommands.Add(CreateCacheBuildCommand());
        cacheCommand.Subcommands.Add(CreateCacheListCommand());
        cacheCommand.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return cacheCommand;
    }

    private static Command CreateCacheBuildCommand()
    {
        var inputPathArgument = new Argument<string>("path-to-sln-or-csproj")
        {
            Description = "Path to the .sln or .csproj file to analyze."
        };
        var dbOption = CreateDbOption();

        var buildCommand = new Command("build", "Build a new complete call-graph snapshot into a SQLite cache.");
        buildCommand.Arguments.Add(inputPathArgument);
        buildCommand.Options.Add(dbOption);
        buildCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new CacheBuildOptions
            {
                InputPath = parseResult.GetValue(inputPathArgument)!,
                DbPath = parseResult.GetValue(dbOption)!
            };

            return await RunCacheBuildAsync(options, cancellationToken);
        });

        return buildCommand;
    }

    private static Command CreateCacheListCommand()
    {
        var dbOption = CreateDbOption();

        var listCommand = new Command("list", "List snapshots in a SQLite call-graph cache.");
        listCommand.Options.Add(dbOption);
        listCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var options = new CacheListOptions
            {
                DbPath = parseResult.GetValue(dbOption)!
            };

            return await RunCacheListAsync(options, cancellationToken);
        });

        return listCommand;
    }

    private static Command CreateTreeCommand()
    {
        var dbOption = CreateDbOption();
        var symbolOption = new Option<string>("--symbol")
        {
            Description = "Fully qualified method signature to use as the root symbol.",
            Required = true
        };
        var snapshotOption = new Option<long?>("--snapshot")
        {
            Description = "Read a specific snapshot id instead of the active snapshot."
        };
        snapshotOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<long?>();
            if (value is <= 0)
            {
                result.AddError("--snapshot must be a positive integer.");
            }
        });

        var maxDepthOption = new Option<int?>("--max-depth")
        {
            Description = "Limit recursive expansion depth."
        };
        maxDepthOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<int?>();
            if (value is <= 0)
            {
                result.AddError("--max-depth must be a positive integer.");
            }
        });

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: text or html.",
            DefaultValueFactory = _ => "text"
        };
        formatOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (!string.IsNullOrWhiteSpace(value) && !TryParseFormat(value, out _))
            {
                result.AddError($"Unknown format '{value}'. Supported values: text, html.");
            }
        });

        var viewOption = new Option<string>("--view")
        {
            Description = "Directional method view: callees, callers, or both.",
            DefaultValueFactory = _ => "callees"
        };
        viewOption.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (!string.IsNullOrWhiteSpace(value) && !TryParseView(value, out _))
            {
                result.AddError($"Unknown view '{value}'. Supported values: callees, callers, both.");
            }
        });

        var outputPathOption = new Option<string?>("--out")
        {
            Description = "Write output to a file instead of stdout."
        };
        var noColorOption = new Option<bool>("--no-color")
        {
            Description = "Disable ANSI color output for text rendering."
        };

        var treeCommand = new Command("tree", "Render a static call tree from a SQLite call-graph snapshot.");
        treeCommand.Options.Add(dbOption);
        treeCommand.Options.Add(symbolOption);
        treeCommand.Options.Add(snapshotOption);
        treeCommand.Options.Add(maxDepthOption);
        treeCommand.Options.Add(formatOption);
        treeCommand.Options.Add(viewOption);
        treeCommand.Options.Add(outputPathOption);
        treeCommand.Options.Add(noColorOption);
        treeCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = parseResult.GetValue(formatOption) ?? "text";
            TryParseFormat(formatValue, out var format);
            var viewValue = parseResult.GetValue(viewOption) ?? "callees";
            TryParseView(viewValue, out var view);

            var options = new TreeOptions
            {
                DbPath = parseResult.GetValue(dbOption)!,
                Symbol = parseResult.GetValue(symbolOption)!,
                SnapshotId = parseResult.GetValue(snapshotOption),
                MaxDepth = parseResult.GetValue(maxDepthOption),
                Format = format,
                View = view,
                OutputPath = parseResult.GetValue(outputPathOption),
                NoColor = parseResult.GetValue(noColorOption)
            };

            return await RunTreeAsync(options, cancellationToken);
        });

        return treeCommand;
    }

    private static Option<string> CreateDbOption()
    {
        return new Option<string>("--db")
        {
            Description = "Path to the SQLite call-graph cache.",
            Required = true
        };
    }

    private static async Task<int> RunCacheBuildAsync(CacheBuildOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new CallGraphBuilder();
            var result = await builder.BuildAsync(options.InputPath, cancellationToken);
            var snapshotId = await new SqliteGraphCache().WriteSnapshotAsync(options.DbPath, result, cancellationToken);

            Console.Error.WriteLine($"Created snapshot {snapshotId.ToString(CultureInfo.InvariantCulture)} in {Path.GetFullPath(options.DbPath)}");
            foreach (var diagnostic in result.Diagnostics.Distinct(StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"warning: {diagnostic}");
            }

            return 0;
        }
        catch (DotTraceException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunCacheListAsync(CacheListOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var snapshots = await new SqliteGraphCache().ListSnapshotsAsync(options.DbPath, cancellationToken);
            Console.WriteLine("active\tid\tcreated_utc\ttool_version\tinput_path\tworkspace_fingerprint");
            foreach (var snapshot in snapshots)
            {
                Console.WriteLine(string.Join(
                    '\t',
                    snapshot.IsActive ? "*" : string.Empty,
                    snapshot.Id.ToString(CultureInfo.InvariantCulture),
                    snapshot.CreatedUtc.ToString("O", CultureInfo.InvariantCulture),
                    snapshot.ToolVersion,
                    snapshot.InputPath,
                    snapshot.WorkspaceFingerprint));
            }

            return 0;
        }
        catch (DotTraceException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunTreeAsync(TreeOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var document = await new SqliteGraphCache().ProjectDocumentAsync(
                options.DbPath,
                options.Symbol,
                new AnalysisOptions(options.MaxDepth),
                options.SnapshotId,
                cancellationToken);
            var output = Render(document, options);

            if (options.OutputPath is null)
            {
                Console.Write(output);
            }
            else
            {
                var fullOutputPath = Path.GetFullPath(options.OutputPath);
                var directory = Path.GetDirectoryName(fullOutputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(fullOutputPath, output, Encoding.UTF8, cancellationToken);
                Console.Error.WriteLine($"Wrote {options.Format} output to {fullOutputPath}");
            }

            return 0;
        }
        catch (DotTraceException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string Render(CallTreeDocument document, TreeOptions options)
    {
        return options.Format switch
        {
            OutputFormat.Html => new HtmlTreeRenderer().RenderDocument(document, options.View),
            _ => new TextTreeRenderer().Render(
                document,
                options.View,
                new RenderOptions(UseColor: !options.NoColor && options.OutputPath is null && !Console.IsOutputRedirected))
        };
    }

    private static bool TryParseFormat(string value, out OutputFormat format)
    {
        switch (value.ToLowerInvariant())
        {
            case "text":
                format = OutputFormat.Text;
                return true;
            case "html":
                format = OutputFormat.Html;
                return true;
            default:
                format = OutputFormat.Text;
                return false;
        }
    }

    private static bool TryParseView(string value, out CallTreeView view)
    {
        switch (value.ToLowerInvariant())
        {
            case "callees":
                view = CallTreeView.Callees;
                return true;
            case "callers":
                view = CallTreeView.Callers;
                return true;
            case "both":
                view = CallTreeView.Both;
                return true;
            default:
                view = CallTreeView.Callees;
                return false;
        }
    }

    private static string[] NormalizeArgs(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            return args;
        }

        return args.Length == 1
            ? ["--help"]
            : [.. args[1..], "--help"];
    }

    private sealed class CacheBuildOptions
    {
        public string InputPath { get; set; } = string.Empty;

        public string DbPath { get; set; } = string.Empty;
    }

    private sealed class CacheListOptions
    {
        public string DbPath { get; set; } = string.Empty;
    }

    private sealed class TreeOptions
    {
        public string DbPath { get; set; } = string.Empty;

        public string Symbol { get; set; } = string.Empty;

        public long? SnapshotId { get; set; }

        public int? MaxDepth { get; set; }

        public OutputFormat Format { get; set; } = OutputFormat.Text;

        public CallTreeView View { get; set; } = CallTreeView.Callees;

        public string? OutputPath { get; set; }

        public bool NoColor { get; set; }
    }

    private enum OutputFormat
    {
        Text,
        Html
    }
}
