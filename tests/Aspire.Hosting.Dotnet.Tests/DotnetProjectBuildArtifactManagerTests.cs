// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectBuildArtifactManagerTests(ITestOutputHelper outputHelper)
{
    private static readonly DateTimeOffset s_startTime = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InactiveBuildProjectIsPrunedOnlyAfterGracePeriod()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider(s_startTime);
        var buildDirectory = Path.Combine(workspace.Path, ".aspire", "build");
        var inactiveHash = "aaaaaaaaaaaa";
        string inactiveBuildProjectPath;

        using (var firstManager = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider))
        {
            inactiveBuildProjectPath = await PublishAsync(firstManager, inactiveHash);
        }

        using var sweeper = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);
        await PublishAsync(sweeper, "bbbbbbbbbbbb");
        Assert.True(File.Exists(inactiveBuildProjectPath));

        timeProvider.Advance(DotnetProjectBuildArtifactManager.InactiveRetentionPeriod - TimeSpan.FromMinutes(1));
        await PublishAsync(sweeper, "bbbbbbbbbbbb");
        Assert.True(File.Exists(inactiveBuildProjectPath));

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await PublishAsync(sweeper, "bbbbbbbbbbbb");
        Assert.False(File.Exists(inactiveBuildProjectPath));
    }

    [Fact]
    public async Task BuildProjectWithoutStateStartsFreshGracePeriodBeforePruning()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider(s_startTime);
        var buildDirectory = Path.Combine(workspace.Path, ".aspire", "build");
        const string inactiveHash = "aaaaaaaaaaaa";
        string inactiveBuildProjectPath;
        string inactiveStatePath;

        using (var firstManager = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider))
        {
            inactiveBuildProjectPath = await PublishAsync(firstManager, inactiveHash);
            inactiveStatePath = firstManager.GetStatePath(inactiveHash);
        }

        Directory.Delete(Path.Combine(buildDirectory, ".artifacts"), recursive: true);

        using var sweeper = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);
        await PublishAsync(sweeper, "bbbbbbbbbbbb");
        Assert.True(File.Exists(inactiveBuildProjectPath));
        Assert.True(File.Exists(inactiveStatePath));

        timeProvider.Advance(DotnetProjectBuildArtifactManager.InactiveRetentionPeriod - TimeSpan.FromMinutes(1));
        await PublishAsync(sweeper, "bbbbbbbbbbbb");
        Assert.True(File.Exists(inactiveBuildProjectPath));

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await PublishAsync(sweeper, "bbbbbbbbbbbb");
        Assert.False(File.Exists(inactiveBuildProjectPath));
        Assert.False(File.Exists(inactiveStatePath));
    }

    [Fact]
    public async Task SharedBuildProjectRemainsUntilEveryLeaseEndsAndThenStartsFreshGracePeriod()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider(s_startTime);
        var buildDirectory = Path.Combine(workspace.Path, ".aspire", "build");
        var sharedHash = "aaaaaaaaaaaa";
        var firstManager = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);
        var secondManager = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);
        using var sweeper = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);

        try
        {
            var sharedBuildProjectPath = await PublishAsync(firstManager, sharedHash);
            Assert.Equal(sharedBuildProjectPath, await PublishAsync(secondManager, sharedHash));

            firstManager.Dispose();
            timeProvider.Advance(DotnetProjectBuildArtifactManager.InactiveRetentionPeriod + TimeSpan.FromDays(2));
            await PublishAsync(sweeper, "bbbbbbbbbbbb");
            Assert.True(File.Exists(sharedBuildProjectPath));

            secondManager.Dispose();
            await PublishAsync(sweeper, "bbbbbbbbbbbb");
            Assert.True(File.Exists(sharedBuildProjectPath));

            timeProvider.Advance(DotnetProjectBuildArtifactManager.InactiveRetentionPeriod + TimeSpan.FromMinutes(1));
            await PublishAsync(sweeper, "bbbbbbbbbbbb");
            Assert.False(File.Exists(sharedBuildProjectPath));
        }
        finally
        {
            firstManager.Dispose();
            secondManager.Dispose();
        }
    }

    [Fact]
    public async Task InvalidStateRetainsBuildProject()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider(s_startTime);
        var buildDirectory = Path.Combine(workspace.Path, ".aspire", "build");
        var hash = "aaaaaaaaaaaa";
        string buildProjectPath;
        string statePath;

        using (var firstManager = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider))
        {
            buildProjectPath = await PublishAsync(firstManager, hash);
            statePath = firstManager.GetStatePath(hash);
        }

        File.WriteAllText(statePath, "future-schema");
        timeProvider.Advance(DotnetProjectBuildArtifactManager.InactiveRetentionPeriod + TimeSpan.FromDays(2));

        using var sweeper = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);
        await PublishAsync(sweeper, "bbbbbbbbbbbb");

        Assert.True(File.Exists(buildProjectPath));
        Assert.Equal("future-schema", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task MissingBuildProjectIsRegeneratedWhileLeaseIsHeld()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var buildDirectory = Path.Combine(workspace.Path, ".aspire", "build");
        using var manager = new DotnetProjectBuildArtifactManager(buildDirectory, TimeProvider.System);

        var buildProjectPath = await PublishAsync(manager, "aaaaaaaaaaaa");
        File.Delete(buildProjectPath);

        Assert.Equal(buildProjectPath, await PublishAsync(manager, "aaaaaaaaaaaa"));
        Assert.True(File.Exists(buildProjectPath));
    }

    [Fact]
    public async Task GeneratedBuildDirectoryIgnoresArtifactsWithoutOverwritingUserFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var generatedDirectory = Path.Combine(workspace.Path, "generated");
        using (var generatedManager = new DotnetProjectBuildArtifactManager(generatedDirectory, TimeProvider.System))
        {
            await PublishAsync(generatedManager, "aaaaaaaaaaaa");
        }

        Assert.Equal("*\n", File.ReadAllText(Path.Combine(generatedDirectory, ".gitignore")));

        var customDirectory = Path.Combine(workspace.Path, "custom");
        Directory.CreateDirectory(customDirectory);
        var customGitIgnorePath = Path.Combine(customDirectory, ".gitignore");
        File.WriteAllText(customGitIgnorePath, "keep-me\n");
        using (var customManager = new DotnetProjectBuildArtifactManager(customDirectory, TimeProvider.System))
        {
            await PublishAsync(customManager, "bbbbbbbbbbbb");
        }

        Assert.Equal("keep-me\n", File.ReadAllText(customGitIgnorePath));
    }

    [Fact]
    public async Task OnlyOldTemporaryFilesArePruned()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider(s_startTime);
        var buildDirectory = Path.Combine(workspace.Path, ".aspire", "build");
        Directory.CreateDirectory(buildDirectory);
        var oldTemporaryPath = Path.Combine(buildDirectory, ".old.tmp");
        var recentTemporaryPath = Path.Combine(buildDirectory, ".recent.tmp");
        File.WriteAllText(oldTemporaryPath, "old");
        File.WriteAllText(recentTemporaryPath, "recent");
        File.SetLastWriteTimeUtc(
            oldTemporaryPath,
            (s_startTime - DotnetProjectBuildArtifactManager.TemporaryFileRetentionPeriod - TimeSpan.FromMinutes(1)).UtcDateTime);
        File.SetLastWriteTimeUtc(recentTemporaryPath, s_startTime.UtcDateTime);

        using var manager = new DotnetProjectBuildArtifactManager(buildDirectory, timeProvider);
        await PublishAsync(manager, "aaaaaaaaaaaa");

        Assert.False(File.Exists(oldTemporaryPath));
        Assert.True(File.Exists(recentTemporaryPath));
    }

    private static Task<string> PublishAsync(DotnetProjectBuildArtifactManager manager, string hash) =>
        manager.PublishAndLeaseAsync(hash, [1, 2, 3], NullLogger.Instance, TestContext.Current.CancellationToken);
}
