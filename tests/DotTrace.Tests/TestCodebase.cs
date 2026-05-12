using System.Diagnostics;

namespace DotTrace.Tests;

internal sealed class TestCodebase : IDisposable
{
    private TestCodebase(string rootPath, string projectPath, string? libraryProjectPath = null)
    {
        RootPath = rootPath;
        ProjectPath = projectPath;
        LibraryProjectPath = libraryProjectPath;
    }

    public string RootPath { get; }

    public string ProjectPath { get; }

    public string? LibraryProjectPath { get; }

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

public sealed class RepeatedCallers
{
    public void Root()
    {
        Left();
        Right();
    }

    public void Left()
    {
        Leaf();
    }

    public void Right()
    {
        Leaf();
    }

    public void Leaf()
    {
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

    public static async Task<TestCodebase> CreateMapAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "dottrace-tests", Guid.NewGuid().ToString("N"));
        var appDirectory = Path.Combine(rootPath, "Sample.App");
        var libraryDirectory = Path.Combine(rootPath, "Sample.Library");

        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(libraryDirectory);

        var libraryProjectPath = Path.Combine(libraryDirectory, "Sample.Library.csproj");
        await File.WriteAllTextAsync(
            libraryProjectPath,
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
            Path.Combine(libraryDirectory, "LibraryWorker.cs"),
"""
namespace Sample.Library;

public sealed class LibraryWorker
{
    public void Work()
    {
    }
}

public sealed class LibraryRoot
{
    public void Start()
    {
        new LibraryWorker().Work();
    }
}
""");

        var appProjectPath = Path.Combine(appDirectory, "Sample.App.csproj");
        await File.WriteAllTextAsync(
            appProjectPath,
"""
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Sample.Library\Sample.Library.csproj" />
  </ItemGroup>
</Project>
""");

        await File.WriteAllTextAsync(
            Path.Combine(appDirectory, "Program.cs"),
"""
using Microsoft.AspNetCore.Mvc;
using Sample.Library;

namespace Sample.App;

public static class Program
{
    public static void Main(string[] args)
    {
        new AppRunner().Run();
    }
}

[ApiController]
[Route("api/[controller]")]
public sealed class ItemsController : ControllerBase
{
    [HttpGet]
    public string Get()
    {
        return new AppRunner().ControllerPath();
    }
}

public sealed class AppRunner
{
    public void Run()
    {
        new LibraryWorker().Work();
    }

    public string ControllerPath()
    {
        return "ok";
    }
}

public sealed class Island
{
    public void A()
    {
        B();
    }

    public void B()
    {
        A();
    }
}
""");

        await RestoreAsync(appProjectPath);
        return new TestCodebase(rootPath, appProjectPath, libraryProjectPath);
    }

    public static async Task<TestCodebase> CreateTopLevelProgramAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "dottrace-tests", Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(rootPath, "Sample.TopLevel");

        Directory.CreateDirectory(projectDirectory);

        var projectPath = Path.Combine(projectDirectory, "Sample.TopLevel.csproj");
        await File.WriteAllTextAsync(
            projectPath,
"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""");

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "Program.cs"),
"""
new AppRunner().Run();

public sealed class AppRunner
{
    public void Run()
    {
        Leaf();
    }

    public void Leaf()
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
