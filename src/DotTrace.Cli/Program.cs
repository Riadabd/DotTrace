using System.CommandLine;
using System.CommandLine.Help;
using System.Text;
using DotTrace.Core.Analysis;
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
        rootCommand.Subcommands.Add(CreateTreeCommand());
        rootCommand.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return rootCommand;
    }

    private static Command CreateTreeCommand()
    {
        var inputPathArgument = new Argument<string>("path-to-sln-or-csproj")
        {
            Description = "Path to the .sln or .csproj file to analyze."
        };
        var symbolOption = new Option<string>("--symbol")
        {
            Description = "Fully qualified method signature to use as the root symbol.",
            Required = true
        };
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

        var outputPathOption = new Option<string?>("--out")
        {
            Description = "Write output to a file instead of stdout."
        };
        var noColorOption = new Option<bool>("--no-color")
        {
            Description = "Disable ANSI color output for text rendering."
        };

        var treeCommand = new Command("tree", "Analyze a solution or project and render a static call tree.");
        treeCommand.Arguments.Add(inputPathArgument);
        treeCommand.Options.Add(symbolOption);
        treeCommand.Options.Add(maxDepthOption);
        treeCommand.Options.Add(formatOption);
        treeCommand.Options.Add(outputPathOption);
        treeCommand.Options.Add(noColorOption);
        treeCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            var formatValue = parseResult.GetValue(formatOption) ?? "text";
            TryParseFormat(formatValue, out var format);

            var options = new CliOptions
            {
                InputPath = parseResult.GetValue(inputPathArgument)!,
                Symbol = parseResult.GetValue(symbolOption)!,
                MaxDepth = parseResult.GetValue(maxDepthOption),
                Format = format,
                OutputPath = parseResult.GetValue(outputPathOption),
                NoColor = parseResult.GetValue(noColorOption)
            };

            return await RunTreeAsync(options, cancellationToken);
        });

        return treeCommand;
    }

    private static async Task<int> RunTreeAsync(CliOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var analyzer = new CallTreeAnalyzer(new AnalysisOptions(options.MaxDepth));
            var result = await analyzer.AnalyzeAsync(options.InputPath, options.Symbol, cancellationToken);
            var output = Render(result.Root, options);

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

    private static string Render(CallTreeNode root, CliOptions options)
    {
        return options.Format switch
        {
            OutputFormat.Html => new HtmlTreeRenderer().RenderDocument(root),
            _ => new TextTreeRenderer().Render(
                root,
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

    private sealed class CliOptions
    {
        public string InputPath { get; set; } = string.Empty;

        public string Symbol { get; set; } = string.Empty;

        public int? MaxDepth { get; set; }

        public OutputFormat Format { get; set; } = OutputFormat.Text;

        public string? OutputPath { get; set; }

        public bool NoColor { get; set; }
    }

    private enum OutputFormat
    {
        Text,
        Html
    }
}
