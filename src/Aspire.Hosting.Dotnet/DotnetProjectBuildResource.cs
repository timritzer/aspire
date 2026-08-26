// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Builds the traditional .NET projects in a distributed application before they start.
/// </summary>
internal sealed class DotnetProjectBuildResource : ExecutableResource, IDisposable
{
    private readonly object _lock = new();
    private readonly List<string> _projectPaths = [];
    private readonly Dictionary<string, string> _projectPathsByIdentity = new(StringComparer.Ordinal);
    private readonly DotnetProjectBuildArtifactManager _artifactManager;
    private bool _solutionGenerationStarted;

    public DotnetProjectBuildResource(string name, string workingDirectory, TimeProvider timeProvider)
        : base(name, "dotnet", workingDirectory)
    {
        SolutionDirectory = Path.Combine(workingDirectory, ".aspire", "build");
        _artifactManager = new DotnetProjectBuildArtifactManager(SolutionDirectory, timeProvider);
    }

    /// <summary>
    /// Gets the AppHost-local directory that contains generated solutions.
    /// </summary>
    public string SolutionDirectory { get; }

    /// <summary>
    /// Gets the project paths included in the generated solution.
    /// </summary>
    public IReadOnlyList<string> ProjectPaths
    {
        get
        {
            lock (_lock)
            {
                return [.. _projectPaths];
            }
        }
    }

    /// <summary>
    /// Adds a project to the generated solution and returns the path used by the coordinated build.
    /// </summary>
    public string AddProject(string projectPath)
    {
        var fullPath = PathNormalizer.ResolveToFilesystemPath(Path.GetFullPath(projectPath));
        // Keep the first path spelling in the solution so it stays relative to the AppHost directory,
        // but deduplicate by physical identity so a symlink alias cannot build the same project twice.
        // Every resource for that identity must launch this returned path so MSBuild uses the same
        // project directory, intermediate outputs, and final output that the coordinated build used.
        var projectIdentity = PathNormalizer.ResolveSymlinks(fullPath);

        lock (_lock)
        {
            if (_solutionGenerationStarted)
            {
                throw new InvalidOperationException("Projects cannot be added after the coordinated build solution has been generated.");
            }

            if (_projectPathsByIdentity.TryGetValue(projectIdentity, out var coordinatedProjectPath))
            {
                return coordinatedProjectPath;
            }

            _projectPathsByIdentity.Add(projectIdentity, fullPath);
            _projectPaths.Add(fullPath);
            return fullPath;
        }
    }

    /// <summary>
    /// Writes the generated solution to the AppHost-local build directory.
    /// </summary>
    public Task<string> WriteSolutionAsync(ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectPaths;
        lock (_lock)
        {
            _solutionGenerationStarted = true;
            projectPaths = [.. _projectPaths];
        }

        // The argument callback already caches successful evaluation for one start attempt. Regenerate here
        // on later attempts so cancellation, transient I/O failures, or cache cleanup cannot poison restarts.
        return WriteSolutionCoreAsync(projectPaths, logger, cancellationToken);
    }

    /// <summary>
    /// Registers process-lifetime cleanup for the generated-solution leases.
    /// </summary>
    public void RegisterForShutdown(IHostApplicationLifetime applicationLifetime)
    {
        _artifactManager.RegisterForShutdown(applicationLifetime);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _artifactManager.Dispose();
    }

    private async Task<string> WriteSolutionCoreAsync(
        IReadOnlyList<string> projectPaths,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var solution = new SolutionModel();

        foreach (var projectPath in projectPaths)
        {
            solution.AddProject(Path.GetRelativePath(SolutionDirectory, projectPath));
        }

        using var solutionStream = new MemoryStream();
        await SolutionSerializers.SlnXml.SaveAsync(solutionStream, solution, cancellationToken).ConfigureAwait(false);
        var solutionBytes = solutionStream.ToArray();

        var hash = new XxHash3();
        hash.Append(solutionBytes);
        var hashString = Convert.ToHexString(hash.GetCurrentHash())[..12].ToLowerInvariant();

        return await _artifactManager.PublishAndLeaseAsync(
            hashString,
            solutionBytes,
            logger,
            cancellationToken).ConfigureAwait(false);
    }
}
