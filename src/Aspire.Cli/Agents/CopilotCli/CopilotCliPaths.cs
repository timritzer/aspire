// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.CopilotCli;

/// <summary>
/// Resolves user-level GitHub Copilot CLI paths.
/// </summary>
internal static class CopilotCliPaths
{
    private const string CopilotHomeEnvironmentVariable = "COPILOT_HOME";
    private const string DefaultCopilotDirectoryName = ".copilot";

    internal static (DirectoryInfo RootDirectory, string RelativePath, bool UsesConfiguredHome) ResolveUserPath(
        DirectoryInfo homeDirectory,
        IEnvironment environment,
        string relativePath)
    {
        // Copilot CLI relocates its entire user configuration root when COPILOT_HOME is set.
        // Keep the child path relative so callers can validate every component below that trusted root.
        var configuredHome = environment.GetEnvironmentVariable(CopilotHomeEnvironmentVariable);
        return !string.IsNullOrEmpty(configuredHome)
            ? (new DirectoryInfo(configuredHome), relativePath, true)
            : (homeDirectory, Path.Combine(DefaultCopilotDirectoryName, relativePath), false);
    }
}
