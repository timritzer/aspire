// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Builds the traditional .NET projects in a distributed application before they start.
/// </summary>
internal sealed class DotnetProjectBuildResource : ExecutableResource
{
    private readonly object _lock = new();
    private readonly List<string> _projectPaths = [];
    private bool _solutionGenerationStarted;

    public DotnetProjectBuildResource(string name, string workingDirectory)
        : base(name, "dotnet", workingDirectory)
    {
        SolutionDirectory = Path.Combine(workingDirectory, ".aspire", "build");
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
    /// Adds a project to the generated solution.
    /// </summary>
    public void AddProject(string projectPath)
    {
        var fullPath = PathNormalizer.ResolveToFilesystemPath(Path.GetFullPath(projectPath));

        lock (_lock)
        {
            if (_solutionGenerationStarted)
            {
                throw new InvalidOperationException("Projects cannot be added after the coordinated build solution has been generated.");
            }

            if (!_projectPaths.Contains(fullPath, StringComparer.Ordinal))
            {
                _projectPaths.Add(fullPath);
            }
        }
    }

    /// <summary>
    /// Writes the generated solution to the AppHost-local build directory.
    /// </summary>
    public Task<string> WriteSolutionAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectPaths;
        lock (_lock)
        {
            _solutionGenerationStarted = true;
            projectPaths = [.. _projectPaths];
        }

        // The argument callback already caches successful evaluation for one start attempt. Regenerate here
        // on later attempts so cancellation, transient I/O failures, or cache cleanup cannot poison restarts.
        return WriteSolutionCoreAsync(projectPaths, cancellationToken);
    }

    private async Task<string> WriteSolutionCoreAsync(
        IReadOnlyList<string> projectPaths,
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

        Directory.CreateDirectory(SolutionDirectory);
        var solutionPath = Path.Combine(SolutionDirectory, $"projects.{hashString}.slnx");
        if (File.Exists(solutionPath))
        {
            return solutionPath;
        }

        // Multiple isolated AppHost instances can share this directory. Write to a unique file on the
        // same volume, then move it atomically so no build can observe a partially-written solution.
        var temporaryPath = Path.Combine(SolutionDirectory, $".{Path.GetRandomFileName()}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, solutionBytes, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, solutionPath);
            }
            catch (IOException) when (File.Exists(solutionPath))
            {
                // Another AppHost instance published the same content first.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return solutionPath;
    }
}
