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
        Assert.Contains("=> Sample.Worker.Step() [target]", bothOutput, StringComparison.Ordinal);
        Assert.Contains("Callees", bothOutput, StringComparison.Ordinal);
        AssertOrdered(bothOutput, "Callers", "=> Sample.Worker.Step() [target]", "Callees");

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

    [Fact]
    public async Task Cli_map_renders_project_scoped_text_and_html()
    {
        using var fixture = await TestCodebase.CreateMapAsync();
        var dbPath = Path.Combine(fixture.RootPath, "graph.db");
        var htmlPath = Path.Combine(fixture.RootPath, "map.html");

        var buildExitCode = await RunCliAsync("cache", "build", fixture.ProjectPath, "--db", dbPath);
        Assert.Equal(0, buildExitCode);

        var (missingProjectExitCode, _, missingProjectError) = await CaptureCliAsync(
            "map",
            "--db",
            dbPath,
            "--no-color");
        Assert.NotEqual(0, missingProjectExitCode);
        Assert.Contains("--project is required", missingProjectError, StringComparison.Ordinal);
        Assert.Contains("Sample.App", missingProjectError, StringComparison.Ordinal);

        var (textExitCode, textOutput, _) = await CaptureCliAsync(
            "map",
            "--db",
            dbPath,
            "--project",
            "Sample.App",
            "--no-color");
        Assert.Equal(0, textExitCode);
        Assert.Contains("Map: Sample.App", textOutput, StringComparison.Ordinal);
        Assert.Contains("Compiler entry points", textOutput, StringComparison.Ordinal);
        Assert.Contains("ASP.NET controller actions", textOutput, StringComparison.Ordinal);
        Assert.Contains("Work() [boundary]", textOutput, StringComparison.Ordinal);

        var htmlExitCode = await RunCliAsync(
            "map",
            "--db",
            dbPath,
            "--project",
            "Sample.App",
            "--format",
            "html",
            "--out",
            htmlPath);
        Assert.Equal(0, htmlExitCode);

        var html = await File.ReadAllTextAsync(htmlPath);
        Assert.Contains("DotTrace Codebase Map", html, StringComparison.Ordinal);
        Assert.Contains("Map: Sample.App", html, StringComparison.Ordinal);
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

    private static void AssertOrdered(string value, string first, string second, string third)
    {
        var firstIndex = value.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = value.IndexOf(second, StringComparison.Ordinal);
        var thirdIndex = value.IndexOf(third, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0);
        Assert.True(secondIndex > firstIndex);
        Assert.True(thirdIndex > secondIndex);
    }
}

[CollectionDefinition("Console")]
public sealed class ConsoleCollection;
