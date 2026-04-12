using System.Diagnostics;
using DotTrace.Core.Analysis;
using Xunit;

namespace DotTrace.Tests;

public sealed class CallTreeAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_reads_csproj_and_builds_source_and_external_nodes()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var analyzer = new CallTreeAnalyzer();

        var result = await analyzer.AnalyzeAsync(fixture.ProjectPath, "Sample.EntryPoint.Run()");

        Assert.Equal(CallTreeNodeKind.Source, result.Root.Kind);
        Assert.Equal("Sample.EntryPoint.Run()", result.Root.DisplayText);
        Assert.Contains(result.Root.Children, child => child.DisplayText == "Sample.Worker.Worker()");
        Assert.Contains(result.Root.Children, child => child.DisplayText == "Sample.Worker.Step()");
        Assert.Contains(result.Root.Children, child => child.Kind == CallTreeNodeKind.External && child.DisplayText.StartsWith("System.Console.WriteLine(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_reads_sln_and_detects_cycles()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var analyzer = new CallTreeAnalyzer();

        var result = await analyzer.AnalyzeAsync(fixture.SolutionPath, "Sample.Worker.Step()");
        var loop = FindNode(result.Root, "Sample.Worker.Loop()");

        Assert.NotNull(loop);
        Assert.Contains(loop!.Children, child => child.Kind == CallTreeNodeKind.Cycle && child.DisplayText == "Sample.Worker.Loop()");
    }

    [Fact]
    public async Task AnalyzeAsync_reports_ambiguous_root_when_signature_is_incomplete()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var analyzer = new CallTreeAnalyzer();

        var exception = await Assert.ThrowsAsync<DotTraceException>(() => analyzer.AnalyzeAsync(fixture.ProjectPath, "Sample.Overloads.Execute"));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sample.Overloads.Execute()", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Sample.Overloads.Execute(System.String)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_honors_max_depth()
    {
        using var fixture = await TestCodebase.CreateAsync();
        var analyzer = new CallTreeAnalyzer(new AnalysisOptions(maxDepth: 1));

        var result = await analyzer.AnalyzeAsync(fixture.ProjectPath, "Sample.EntryPoint.Run()");

        Assert.Contains(result.Root.Children, child => child.DisplayText == "Sample.Worker.Step()" && child.Kind == CallTreeNodeKind.Truncated);
    }

    private static CallTreeNode? FindNode(CallTreeNode root, string displayText)
    {
        if (root.DisplayText == displayText)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var match = FindNode(child, displayText);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private sealed class TestCodebase : IDisposable
    {
        private TestCodebase(string rootPath, string projectPath, string solutionPath)
        {
            RootPath = rootPath;
            ProjectPath = projectPath;
            SolutionPath = solutionPath;
        }

        public string RootPath { get; }

        public string ProjectPath { get; }

        public string SolutionPath { get; }

        public static async Task<TestCodebase> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "dottrace-tests", Guid.NewGuid().ToString("N"));
            var projectDirectory = Path.Combine(rootPath, "Sample.App");

            Directory.CreateDirectory(projectDirectory);

            var projectPath = Path.Combine(projectDirectory, "Sample.App.csproj");
            await File.WriteAllTextAsync(
                projectPath,
"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""");

            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "EntryPoint.cs"),
"""
using System;

namespace Sample;

public sealed class EntryPoint
{
    public void Run()
    {
        var worker = new Worker();
        worker.Step();
        Console.WriteLine("done");
    }
}

public sealed class Worker
{
    public Worker()
    {
    }

    public void Step()
    {
        Loop();
    }

    public void Loop()
    {
        Loop();
    }
}

public sealed class Overloads
{
    public void Execute()
    {
    }

    public void Execute(string value)
    {
    }
}
""");

            var solutionPath = Path.Combine(rootPath, "Sample.sln");
            await File.WriteAllTextAsync(
                solutionPath,
"""
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Sample.App", "Sample.App\Sample.App.csproj", "{F0A9E2DE-F3C8-49C0-854A-C77AE0CF2878}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{F0A9E2DE-F3C8-49C0-854A-C77AE0CF2878}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{F0A9E2DE-F3C8-49C0-854A-C77AE0CF2878}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{F0A9E2DE-F3C8-49C0-854A-C77AE0CF2878}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{F0A9E2DE-F3C8-49C0-854A-C77AE0CF2878}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
""");

            await RestoreAsync(projectPath);
            return new TestCodebase(rootPath, projectPath, solutionPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static async Task RestoreAsync(string projectPath)
        {
            var startInfo = new ProcessStartInfo("dotnet", $"restore \"{projectPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start dotnet restore.");

            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"dotnet restore failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }
}
