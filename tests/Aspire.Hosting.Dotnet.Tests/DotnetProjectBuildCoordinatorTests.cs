// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectBuildCoordinatorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task MultipleProjectsCreateOneCoordinatedBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = Path.Combine(builder.AppHostDirectory, "Api", "Api.csproj");
        var workerPath = Path.Combine(builder.AppHostDirectory, "Worker", "Worker.csproj");

        var api = builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true);
        var worker = builder.AddDotnetProjectForPolyglot(
            "worker",
            workerPath,
            new ProjectResourceOptions { ExcludeLaunchProfile = true });

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([apiPath, workerPath], buildResource.ProjectPaths);
        AssertBuildDependency(api.Resource, buildResource);
        AssertBuildDependency(worker.Resource, buildResource);
        Assert.Empty(buildResource.Annotations.OfType<ExplicitStartupAnnotation>());
        var hidden = Assert.Single(buildResource.Annotations.OfType<HiddenAnnotation>());
        Assert.Equal(HiddenBehavior.OnCompletion, hidden.Behavior);
        Assert.Equal([0], hidden.SuccessfulExitCodes);
        Assert.Same(
            ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(buildResource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));

        using var app = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(buildResource, app.Services);
        var solutionPath = Assert.IsType<string>(args[1]);
        Assert.Equal(
            Path.Combine(workspace.Path, ".aspire", "build"),
            buildResource.SolutionDirectory);
        Assert.StartsWith(buildResource.SolutionDirectory, solutionPath, StringComparison.Ordinal);
        Assert.True(File.Exists(solutionPath));

        var expected = new List<string> { "build", solutionPath };
        AddExpectedConfiguration(builder, expected);
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinatedBuildIsIndependentOfWatchMode(bool watchEnabled)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration["AppHost:Run:WatchEnabled"] = watchEnabled.ToString();

        var project = builder.AddDotnetProject("api", "Api.csproj", options => options.ExcludeLaunchProfile = true);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        AssertBuildDependency(project.Resource, buildResource);
    }

    [Fact]
    public async Task FileOnlyModelDoesNotCreateCoordinatedBuild()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var filePath = Path.Combine(builder.AppHostDirectory, "worker.cs");

        var file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Empty(file.Resource.Annotations.OfType<WaitAnnotation>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(file.Resource);
        var expected = new List<string> { "run", "--file", filePath, "--no-cache" };
        AddExpectedConfiguration(builder, expected);
        expected.Add("--no-launch-profile");
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedModelBuildsOnlyProjectsAndGatesFileApp(bool fileFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var projectPath = Path.Combine(builder.AppHostDirectory, "Api", "Api.csproj");
        var filePath = Path.Combine(builder.AppHostDirectory, "worker", "worker.cs");

        IResourceBuilder<DotnetProjectResource> project;
        IResourceBuilder<DotnetProjectResource> file;
        if (fileFirst)
        {
            file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);
            project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        }
        else
        {
            project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
            file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);
        }

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([projectPath], buildResource.ProjectPaths);
        AssertBuildDependency(project.Resource, buildResource);
        AssertBuildDependency(file.Resource, buildResource);

        var projectArgs = await ArgumentEvaluator.GetArgumentListAsync(project.Resource);
        Assert.Equal("--no-build", projectArgs[3]);

        var fileArgs = await ArgumentEvaluator.GetArgumentListAsync(file.Resource);
        var expectedFileArgs = new List<string> { "run", "--file", filePath, "--no-cache" };
        AddExpectedConfiguration(builder, expectedFileArgs);
        expectedFileArgs.Add("--no-launch-profile");
        Assert.Equal(expectedFileArgs, fileArgs);
    }

    [Fact]
    public void DuplicateProjectPathsProduceOneSolutionEntryAndOneWait()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var projectPath = Path.Combine(builder.AppHostDirectory, "Api", "Api.csproj");

        var first = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        var second = builder.AddDotnetProject("api-copy", projectPath, options => options.ExcludeLaunchProfile = true);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([projectPath], buildResource.ProjectPaths);
        AssertBuildDependency(first.Resource, buildResource);
        AssertBuildDependency(second.Resource, buildResource);
    }

    [Fact]
    public void PublishModeDoesNotCreateCoordinatedBuildOrSuppressProjectBuild()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddDotnetProject("api", "Api.csproj", options => options.ExcludeLaunchProfile = true);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        var metadata = Assert.Single(project.Resource.Annotations.OfType<DotnetProjectMetadata>());
        Assert.False(metadata.SuppressBuild);
        Assert.Empty(project.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public async Task GeneratedSolutionContainsOnlyUniqueProjectsInModelOrder()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var firstProject = CreateProject(workspace.Path, "First Project", "First.csproj");
        var secondProject = CreateProject(workspace.Path, "Second", "Second.csproj");
        builder.AddDotnetProject("first", firstProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("second", secondProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("first-copy", firstProject, options => options.ExcludeLaunchProfile = true);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());

        var solutionPath = await buildResource.WriteSolutionAsync(TestContext.Current.CancellationToken);

        var solution = await SolutionSerializers.SlnXml.OpenAsync(
            solutionPath,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            ["First.csproj", "Second.csproj"],
            solution.SolutionProjects.Select(project => Path.GetFileName(project.FilePath)));

        var contents = await File.ReadAllTextAsync(solutionPath, TestContext.Current.CancellationToken);
        contents = contents.Replace(
            NormalizeSolutionPath(Path.GetRelativePath(buildResource.SolutionDirectory, firstProject)),
            "First Project/First.csproj",
            StringComparison.Ordinal);
        contents = contents.Replace(
            NormalizeSolutionPath(Path.GetRelativePath(buildResource.SolutionDirectory, secondProject)),
            "Second/Second.csproj",
            StringComparison.Ordinal);

        await Verify(contents, "slnx");
    }

    [Fact]
    public async Task GeneratedSolutionUsesAppHostLocalBuildDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = workspace.Path, outputHelper);
        var projectPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());

        var solutionPath = await buildResource.WriteSolutionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(workspace.Path, ".aspire", "build"), buildResource.SolutionDirectory);
        Assert.StartsWith(buildResource.SolutionDirectory, solutionPath, StringComparison.Ordinal);
        Assert.True(File.Exists(solutionPath));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Windows paths that differ only by casing identify the same project.")]
    public void CaseDistinctProjectPathsFailInsteadOfSilentlySuppressingOneBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var buildResource = new DotnetProjectBuildResource(
            DotnetProjectBuildCoordinator.BuildResourceName,
            workspace.Path);
        var firstPath = Path.Combine(workspace.Path, "Service", "App.csproj");
        var secondPath = Path.Combine(workspace.Path, "service", "App.csproj");

        buildResource.AddProject(firstPath);

        var exception = Assert.Throws<DistributedApplicationException>(
            () => buildResource.AddProject(secondPath));
        Assert.Contains("differ only by letter casing", exception.Message);
    }

    [Fact]
    public async Task CanceledSolutionGenerationCanBeRetried()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var buildResource = new DotnetProjectBuildResource(
            DotnetProjectBuildCoordinator.BuildResourceName,
            workspace.Path);
        buildResource.AddProject(CreateProject(workspace.Path, "Api", "Api.csproj"));
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => buildResource.WriteSolutionAsync(canceledCts.Token));

        var solutionPath = await buildResource.WriteSolutionAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(solutionPath));
    }

    [Theory]
    [InlineData(nameof(KnownResourceStates.Finished), null, false)]
    [InlineData(nameof(KnownResourceStates.Finished), -1, false)]
    [InlineData(nameof(KnownResourceStates.Finished), 0, true)]
    [InlineData(nameof(KnownResourceStates.Finished), 1, true)]
    [InlineData(nameof(KnownResourceStates.FailedToStart), null, true)]
    public void BuildCompletionWaitsForSettledExitCode(string state, int? exitCode, bool expected)
    {
        var snapshot = new CustomResourceSnapshot
        {
            ResourceType = "Executable",
            State = state,
            ExitCode = exitCode,
            Properties = []
        };

        Assert.Equal(expected, DotnetProjectBuildCoordinator.IsSettledBuildSnapshot(snapshot));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task SharedProjectGraphBuildsOnceBeforeServicesRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sharedProject = CreateSharedProject(workspace.Path);
        var apiProject = CreateConsoleProject(workspace.Path, "Api", sharedProject);
        var workerProject = CreateConsoleProject(workspace.Path, "Worker", sharedProject);
        var apiSentinel = Path.Combine(workspace.Path, "api-ran.txt");
        var workerSentinel = Path.Combine(workspace.Path, "worker-ran.txt");

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("api", apiProject, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("SENTINEL_PATH", apiSentinel);
        builder.AddDotnetProject("worker", workerProject, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("SENTINEL_PATH", workerSentinel);

        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceAsync("api", KnownResourceStates.Finished, completionCts.Token),
                app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Finished, completionCts.Token));
        }

        using (var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StopAsync(stopCts.Token);
        }

        Assert.Equal("shared", File.ReadAllText(apiSentinel));
        Assert.Equal("shared", File.ReadAllText(workerSentinel));
        Assert.Single(File.ReadAllLines(GetBuildCountPath(sharedProject)));
        Assert.Single(File.ReadAllLines(GetBuildCountPath(apiProject)));
        Assert.Single(File.ReadAllLines(GetBuildCountPath(workerProject)));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FailedBuildStreamsLogsAndPreventsFileAppFromStarting()
    {
        const string buildLogMarker = "COORDINATED_BUILD_MARKER";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var brokenProject = CreateBrokenProject(workspace.Path, buildLogMarker);
        var fileApp = Path.Combine(workspace.Path, "worker.cs");
        var sentinel = Path.Combine(workspace.Path, "worker-ran.txt");
        File.WriteAllText(fileApp, """
            #!/usr/bin/env dotnet

            System.IO.File.WriteAllText(
                System.Environment.GetEnvironmentVariable("SENTINEL_PATH")!,
                "started");
            """);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("broken", brokenProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("worker", fileApp, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("SENTINEL_PATH", sentinel);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());

        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        ResourceEvent buildEvent;
        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            buildEvent = await app.ResourceNotifications.WaitForResourceAsync(
                DotnetProjectBuildCoordinator.BuildResourceName,
                resourceEvent =>
                    KnownResourceStates.TerminalStates.Contains(resourceEvent.Snapshot.State?.Text) &&
                    resourceEvent.Snapshot.ExitCode is not null,
                completionCts.Token);
            var workerState = await app.ResourceNotifications.WaitForResourceAsync(
                "worker",
                [KnownResourceStates.FailedToStart, KnownResourceStates.Exited, KnownResourceStates.Finished],
                completionCts.Token);

            Assert.NotEqual(0, buildEvent.Snapshot.ExitCode);
            Assert.NotEqual(KnownResourceStates.Finished, workerState);
        }

        using var logsCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var buildLogs = await ReadLogsAsync(
            app.Services.GetRequiredService<ResourceLoggerService>(),
            buildEvent.ResourceId,
            minimumCount: 6,
            logsCts.Token);
        Assert.Contains(buildLogs, line => line.Content.Contains(buildLogMarker, StringComparison.Ordinal));

        Assert.False(File.Exists(sentinel));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    private static string CreateProject(string root, string directoryName, string projectFileName)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, directoryName));
        var path = Path.Combine(directory.FullName, projectFileName);
        File.WriteAllText(path, "<Project />");
        return path;
    }

    private static string CreateSharedProject(string root)
    {
        var projectPath = CreateProjectFile(root, "Shared", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <Target Name="RecordBuild" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "SharedValue.cs"), """
            namespace Shared;

            public static class SharedValue
            {
                public static string Value => "shared";
            }
            """);
        return projectPath;
    }

    private static string CreateConsoleProject(string root, string name, string sharedProject)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name));
        var relativeSharedProject = Path.GetRelativePath(directory.FullName, sharedProject);
        var projectPath = Path.Combine(directory.FullName, $"{name}.csproj");
        File.WriteAllText(projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{relativeSharedProject}}" />
              </ItemGroup>
              <Target Name="RecordBuild" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory.FullName, "Program.cs"), """
            using Shared;

            File.WriteAllText(
                Environment.GetEnvironmentVariable("SENTINEL_PATH")!,
                SharedValue.Value);
            """);
        return projectPath;
    }

    private static string CreateBrokenProject(string root, string buildLogMarker)
    {
        var projectPath = CreateProjectFile(root, "Broken", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <Target Name="EmitBuildMarker" BeforeTargets="CoreCompile">
                <Message Importance="high" Text="{{buildLogMarker}}" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "Program.cs"), "this does not compile");
        return projectPath;
    }

    private static string CreateProjectFile(string root, string name, string contents)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name));
        var projectPath = Path.Combine(directory.FullName, $"{name}.csproj");
        File.WriteAllText(projectPath, contents);
        return projectPath;
    }

    private static string GetBuildCountPath(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath)!, "build-count.txt");

    private static string NormalizeSolutionPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static async Task<IReadOnlyList<LogLine>> ReadLogsAsync(
        ResourceLoggerService loggerService,
        string resourceName,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        var logs = new List<LogLine>();
        await foreach (var batch in loggerService.WatchAsync(resourceName).WithCancellation(cancellationToken))
        {
            logs.AddRange(batch);
            if (logs.Count >= minimumCount)
            {
                return logs;
            }
        }

        return logs;
    }

    private static void AssertBuildDependency(
        DotnetProjectResource resource,
        DotnetProjectBuildResource buildResource)
    {
        var wait = Assert.Single(
            resource.Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, buildResource));
        Assert.Equal(WaitType.WaitForCompletion, wait.WaitType);
        Assert.Equal(0, wait.ExitCode);

        Assert.Single(
            resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, buildResource) &&
                          annotation.Type == "WaitFor");
    }

    private static void AddExpectedConfiguration(IDistributedApplicationBuilder builder, List<string> expected)
    {
        if (builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } configuration)
        {
            expected.Add("--configuration");
            expected.Add(configuration);
        }
    }
}
