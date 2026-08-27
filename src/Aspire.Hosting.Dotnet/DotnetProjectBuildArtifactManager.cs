// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet;

internal sealed class DotnetProjectBuildArtifactManager : IDisposable
{
    private const string SolutionFilePrefix = "projects.";
    private const string SolutionFileExtension = ".slnx";
    private const string LeaseFileExtension = ".lease";
    private const string StateFileExtension = ".state";
    private const string ActiveState = "0";
    private static readonly TimeSpan s_inactiveRetentionPeriod = TimeSpan.FromHours(24);
    private static readonly TimeSpan s_temporaryFileRetentionPeriod = TimeSpan.FromHours(24);

    private readonly object _lock = new();
    private readonly Dictionary<string, HeldFileLease> _leases = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private CancellationTokenRegistration _applicationStoppedRegistration;
    private bool _shutdownRegistered;
    private bool _stopping;
    private bool _disposed;

    public DotnetProjectBuildArtifactManager(string solutionDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(solutionDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        SolutionDirectory = solutionDirectory;
        _timeProvider = timeProvider;
    }

    public string SolutionDirectory { get; }

    internal static TimeSpan InactiveRetentionPeriod => s_inactiveRetentionPeriod;

    internal static TimeSpan TemporaryFileRetentionPeriod => s_temporaryFileRetentionPeriod;

    public async Task<string> PublishAndLeaseAsync(
        string hash,
        byte[] solutionBytes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);
        ArgumentNullException.ThrowIfNull(solutionBytes);
        ArgumentNullException.ThrowIfNull(logger);

        Directory.CreateDirectory(SolutionDirectory);
        var coordinationLockPath = Path.Combine(SolutionDirectory, ".coordination.lock");
        using var coordinationLock = await FileLock.AcquireAsync(coordinationLockPath, cancellationToken).ConfigureAwait(false);

        TryCreateGitIgnore(logger);

        var solutionPath = GetSolutionPath(hash);
        if (!File.Exists(solutionPath))
        {
            await PublishSolutionAsync(solutionPath, solutionBytes, logger, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            ThrowIfUnavailable();

            var acquiredLease = false;
            if (!_leases.ContainsKey(hash))
            {
                var leaseDirectory = GetLeaseDirectory(hash);
                var lease = HeldFileLease.Acquire(
                    leaseDirectory,
                    string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessId}-"),
                    LeaseFileExtension);
                _leases.Add(hash, lease);
                acquiredLease = true;
            }

            try
            {
                // Reset the inactivity clock while holding the same cross-process lock used by the sweeper.
                // If this write fails, fail the build rather than returning a solution that another AppHost
                // could later mistake for continuously inactive.
                WriteState(hash, ActiveState);
            }
            catch
            {
                if (acquiredLease)
                {
                    _leases.Remove(hash, out var lease);
                    lease?.Dispose();
                }

                throw;
            }

            SweepManagedSolutions(logger);
            SweepTemporaryFiles(logger);
        }

        return solutionPath;
    }

    public void RegisterForShutdown(IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);

        lock (_lock)
        {
            ThrowIfUnavailable();
            if (_shutdownRegistered)
            {
                return;
            }

            _shutdownRegistered = true;
        }

        var registration = applicationLifetime.ApplicationStopped.Register(
            static state => ((DotnetProjectBuildArtifactManager)state!).Stop(),
            this);

        var disposeRegistration = false;
        lock (_lock)
        {
            if (_disposed || _stopping)
            {
                disposeRegistration = true;
            }
            else
            {
                _applicationStoppedRegistration = registration;
            }
        }

        if (disposeRegistration)
        {
            // CancellationTokenRegistration.Dispose can wait for an in-flight callback. Never invoke it
            // while holding _lock because the callback enters Stop(), which takes the same lock.
            registration.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenRegistration registration;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registration = _applicationStoppedRegistration;
            DisposeLeases();
        }

        registration.Dispose();
    }

    internal string GetSolutionPath(string hash) =>
        Path.Combine(SolutionDirectory, $"{SolutionFilePrefix}{hash}{SolutionFileExtension}");

    internal string GetStatePath(string hash) =>
        Path.Combine(GetStateDirectory(), $"{hash}{StateFileExtension}");

    private async Task PublishSolutionAsync(
        string solutionPath,
        byte[] solutionBytes,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Different AppHost entry points and launches that bypass the CLI's single-instance handling
        // can share this directory. Publish atomically so no build observes a partial solution.
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
            TryDelete(
                temporaryPath,
                logger,
                "Failed to delete temporary coordinated build solution file '{Path}'.");
        }
    }

    private void TryCreateGitIgnore(ILogger logger)
    {
        var gitIgnorePath = Path.Combine(SolutionDirectory, ".gitignore");
        try
        {
            using var stream = new FileStream(
                gitIgnorePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.None);
            stream.Write("*\n"u8);
        }
        catch (IOException) when (File.Exists(gitIgnorePath))
        {
            // Preserve a user-authored file and tolerate another AppHost winning creation.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to create generated-solution ignore file '{Path}'.", gitIgnorePath);
        }
    }

    private void SweepManagedSolutions(ILogger logger)
    {
        var now = _timeProvider.GetUtcNow();
        SweepSolutionFiles(now, logger);
        SweepOrphanedStateFiles(logger);
    }

    private void SweepSolutionFiles(DateTimeOffset now, ILogger logger)
    {
        string[] solutionPaths;
        try
        {
            solutionPaths = Directory.GetFiles(
                SolutionDirectory,
                $"{SolutionFilePrefix}*{SolutionFileExtension}");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to enumerate coordinated build solutions in '{Path}'.", SolutionDirectory);
            return;
        }

        var inactiveState = now.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        foreach (var solutionPath in solutionPaths)
        {
            var fileName = Path.GetFileName(solutionPath);
            if (!fileName.StartsWith(SolutionFilePrefix, StringComparison.Ordinal) ||
                !fileName.EndsWith(SolutionFileExtension, StringComparison.Ordinal))
            {
                continue;
            }

            var hash = fileName[SolutionFilePrefix.Length..^SolutionFileExtension.Length];
            if (!IsSolutionHash(hash))
            {
                continue;
            }

            var leaseDirectory = GetLeaseDirectory(hash);
            var leaseState = HeldFileLease.Probe(leaseDirectory, LeaseFileExtension);
            if (leaseState is HeldFileLeaseProbeResult.Active)
            {
                TryWriteState(hash, ActiveState, logger);
                continue;
            }

            if (leaseState is HeldFileLeaseProbeResult.Unknown)
            {
                logger.LogDebug("Retaining coordinated build solution '{Hash}' because its lease state is unknown.", hash);
                continue;
            }

            var statePath = GetStatePath(hash);
            if (!File.Exists(statePath))
            {
                // A crash or failed state write can leave the atomically published solution behind.
                // Start a full grace period once state storage is available instead of deleting it immediately.
                TryWriteState(hash, inactiveState, logger);
                continue;
            }

            if (!TryReadState(statePath, out var inactiveObservedUtc))
            {
                logger.LogDebug("Retaining coordinated build solution '{Hash}' because its state file is invalid.", hash);
                continue;
            }

            if (inactiveObservedUtc is null)
            {
                TryWriteState(hash, inactiveState, logger);
                continue;
            }

            if (inactiveObservedUtc > now)
            {
                // Wall clocks can move backwards. Start a fresh grace period instead of deleting early.
                TryWriteState(hash, inactiveState, logger);
                continue;
            }

            if (now - inactiveObservedUtc < s_inactiveRetentionPeriod)
            {
                continue;
            }

            if (TryDelete(
                solutionPath,
                logger,
                "Failed to prune inactive coordinated build solution '{Path}'."))
            {
                TryDelete(statePath, logger, "Failed to delete coordinated build solution state file '{Path}'.");
                TryDeleteEmptyDirectory(leaseDirectory, logger);
            }
        }
    }

    private void SweepOrphanedStateFiles(ILogger logger)
    {
        var stateDirectory = GetStateDirectory();
        string[] statePaths;
        try
        {
            statePaths = Directory.GetFiles(stateDirectory, $"*{StateFileExtension}");
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to enumerate coordinated build solution state in '{Path}'.", stateDirectory);
            return;
        }

        foreach (var statePath in statePaths)
        {
            var hash = Path.GetFileNameWithoutExtension(statePath);
            if (!IsSolutionHash(hash))
            {
                continue;
            }

            if (File.Exists(GetSolutionPath(hash)))
            {
                continue;
            }

            var leaseDirectory = GetLeaseDirectory(hash);
            var leaseState = HeldFileLease.Probe(leaseDirectory, LeaseFileExtension);
            if (leaseState is HeldFileLeaseProbeResult.Active)
            {
                TryWriteState(hash, ActiveState, logger);
                continue;
            }

            if (leaseState is HeldFileLeaseProbeResult.Unknown)
            {
                logger.LogDebug("Retaining coordinated build state '{Hash}' because its lease state is unknown.", hash);
                continue;
            }

            TryDelete(statePath, logger, "Failed to delete orphaned coordinated build state file '{Path}'.");
            TryDeleteEmptyDirectory(leaseDirectory, logger);
        }
    }

    private void SweepTemporaryFiles(ILogger logger)
    {
        string[] temporaryPaths;
        try
        {
            temporaryPaths = Directory.GetFiles(SolutionDirectory, ".*.tmp");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to enumerate temporary coordinated build solution files in '{Path}'.", SolutionDirectory);
            return;
        }

        var cutoff = _timeProvider.GetUtcNow() - s_temporaryFileRetentionPeriod;
        foreach (var temporaryPath in temporaryPaths)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(temporaryPath) <= cutoff.UtcDateTime)
                {
                    TryDelete(
                        temporaryPath,
                        logger,
                        "Failed to prune stale temporary coordinated build solution file '{Path}'.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                logger.LogDebug(ex, "Failed to inspect temporary coordinated build solution file '{Path}'.", temporaryPath);
            }
        }
    }

    private static bool TryReadState(string statePath, out DateTimeOffset? inactiveObservedUtc)
    {
        inactiveObservedUtc = null;

        string state;
        try
        {
            state = File.ReadAllText(statePath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        if (state == ActiveState)
        {
            return true;
        }

        if (!long.TryParse(state, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        {
            return false;
        }

        try
        {
            inactiveObservedUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private void WriteState(string hash, string state)
    {
        var stateDirectory = GetStateDirectory();
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(GetStatePath(hash), state);
    }

    private void TryWriteState(string hash, string state, ILogger logger)
    {
        try
        {
            WriteState(hash, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to update coordinated build solution state for '{Hash}'.", hash);
        }
    }

    private string GetLeaseDirectory(string hash) =>
        Path.Combine(SolutionDirectory, ".leases", "v1", hash);

    private string GetStateDirectory() =>
        Path.Combine(SolutionDirectory, ".artifacts", "v1");

    private static bool IsSolutionHash(string hash) =>
        hash.Length == 12 && hash.All(Uri.IsHexDigit);

    private void Stop()
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            DisposeLeases();
        }
    }

    private void DisposeLeases()
    {
        foreach (var lease in _leases.Values)
        {
            lease.Dispose();
        }

        _leases.Clear();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stopping)
        {
            throw new InvalidOperationException("Coordinated build solution artifacts cannot be acquired while the AppHost is stopping.");
        }
    }

    private static bool TryDelete(string path, ILogger logger, string message)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, message, path);
            return false;
        }
    }

    private static void TryDeleteEmptyDirectory(string path, ILogger logger)
    {
        try
        {
            Directory.Delete(path);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogDebug(ex, "Failed to delete empty coordinated build lease directory '{Path}'.", path);
        }
    }
}
