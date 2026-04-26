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
