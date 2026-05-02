using Xunit;

namespace DotTrace.Tests;

[Collection("Console")]
public sealed class CliSnapshotTests
{
    [Fact]
    public async Task Cli_build_lists_and_renders_html_from_sqlite_snapshot()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");
        var htmlPath = Path.Combine(fixture.RootPath, "trace.html");

        var buildExitCode = await RunCliAsync("cache", "build", fixture.ProjectPath, "--db", dbPath);
        Assert.Equal(0, buildExitCode);

        var (listExitCode, listOutput, _) = await CaptureCliAsync("cache", "list", "--db", dbPath);
        Assert.Equal(0, listExitCode);
        Assert.Contains("*", listOutput, StringComparison.Ordinal);
        Assert.Contains(fixture.ProjectPath, listOutput, StringComparison.Ordinal);

        var treeExitCode = await RunCliAsync(
            "tree",
            "--db",
            dbPath,
            "--symbol",
            "Sample.EntryPoint.Run()",
            "--format",
            "html",
            "--out",
            htmlPath);
        Assert.Equal(0, treeExitCode);

        var html = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("DotTrace Call Tree", html, StringComparison.Ordinal);
        Assert.Contains("Sample.EntryPoint.Run()", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_tree_validates_view_and_preserves_default_callees()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");

        var buildExitCode = await RunCliAsync("cache", "build", fixture.ProjectPath, "--db", dbPath);
        Assert.Equal(0, buildExitCode);

        var (defaultExitCode, defaultOutput, _) = await CaptureCliAsync(
            "tree",
            "--db",
            dbPath,
            "--symbol",
            "Sample.EntryPoint.Run()",
            "--no-color");
        Assert.Equal(0, defaultExitCode);
        Assert.StartsWith("Sample.EntryPoint.Run()", defaultOutput, StringComparison.Ordinal);
        Assert.Contains("Step()", defaultOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Callers", defaultOutput, StringComparison.Ordinal);

        var (callersExitCode, callersOutput, _) = await CaptureCliAsync(
            "tree",
            "--db",
            dbPath,
            "--symbol",
            "Sample.Worker.Step()",
            "--view",
            "callers",
            "--no-color");
        Assert.Equal(0, callersExitCode);
        Assert.Contains("Callers of Sample.Worker.Step()", callersOutput, StringComparison.Ordinal);
        Assert.Contains("Sample.EntryPoint.Run()", callersOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("(calls Step()", callersOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Callees", callersOutput, StringComparison.Ordinal);

        var (bothExitCode, bothOutput, _) = await CaptureCliAsync(
            "tree",
            "--db",
            dbPath,
            "--symbol",
            "Sample.Worker.Step()",
            "--view",
            "both",
            "--no-color");
        Assert.Equal(0, bothExitCode);
        Assert.Contains("Callers", bothOutput, StringComparison.Ordinal);
        Assert.Contains("Callees", bothOutput, StringComparison.Ordinal);

        var (invalidExitCode, _, invalidError) = await CaptureCliAsync(
            "tree",
            "--db",
            dbPath,
            "--symbol",
            "Sample.Worker.Step()",
            "--view",
            "sideways");
        Assert.NotEqual(0, invalidExitCode);
        Assert.Contains("Unknown view 'sideways'", invalidError, StringComparison.Ordinal);
    }

    private static async Task<int> RunCliAsync(params string[] args)
    {
        var (exitCode, _, _) = await CaptureCliAsync(args);
        return exitCode;
    }

    private static async Task<(int ExitCode, string Output, string Error)> CaptureCliAsync(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        try
        {
            var exitCode = await ProgramEntry.RunAsync(args);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

[CollectionDefinition("Console")]
public sealed class ConsoleCollection;
