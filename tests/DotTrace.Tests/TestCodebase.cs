using System.Diagnostics;

namespace DotTrace.Tests;

internal sealed class TestCodebase : IDisposable
{
    private TestCodebase(string rootPath, string projectPath)
    {
        RootPath = rootPath;
        ProjectPath = projectPath;
    }

    public string RootPath { get; }

    public string ProjectPath { get; }

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
using System.Threading;

namespace Sample;

public sealed class EntryPoint
{
    public void Run()
    {
        var worker = new Worker();
        worker.Step();
        Console.WriteLine("done");
    }

    public void Unresolved()
    {
        MissingCall();
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

    public void WithLocal()
    {
        Local();

        void Local()
        {
            Console.WriteLine("local");
        }
    }
}

public sealed class AccessorUser
{
    private int value;

    public int Value
    {
        get => value;
        set => this.value = value;
    }

    public int this[int index]
    {
        get => value + index;
        set => this.value = value + index;
    }

    public void UseAccessors()
    {
        Value = Value + this[0];
        this[1] = Value;
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

public sealed class ArgumentSource
{
    public void Run(string str, CancellationToken ct)
    {
        var target = new ArgumentTarget();
        var number = 1;

        target.Target(str, ct);
        target.Target("done", CancellationToken.None);
        target.Named(cancellationToken: ct, value: str);
        target.RefOut(ref number, out var written);
        target.In(in number);
        target.Optional(str);
        _ = new Constructed(str);
        Constructed implicitConstructed = new(str);
        _ = new DerivedConstructed(str);

        GC.KeepAlive(written);
        GC.KeepAlive(implicitConstructed);
    }
}

public sealed class ArgumentTarget
{
    public void Target(string value, CancellationToken cancellationToken)
    {
    }

    public void Named(string value, CancellationToken cancellationToken)
    {
    }

    public void RefOut(ref int input, out int output)
    {
        output = input;
    }

    public void In(in int input)
    {
    }

    public void Optional(string value, int count = 10)
    {
    }
}

public sealed class Constructed
{
    public Constructed(string value)
    {
    }
}

public class ConstructedBase
{
    public ConstructedBase(string value)
    {
    }
}

public sealed class DerivedConstructed : ConstructedBase
{
    public DerivedConstructed(string value)
        : base(value)
    {
    }
}
""");

        await RestoreAsync(projectPath);
        return new TestCodebase(rootPath, projectPath);
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
