// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Agents.CopilotCli;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents a location where agent asset files can be installed.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Scopes = {Scopes}, Id = {Id}, DisplayName = {DisplayName}, Description = {Description}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetLocation
{
    /// <summary>
    /// Standard <c>.agents/skills/</c> location supported by VS Code, GitHub Copilot, and OpenCode.
    /// </summary>
    public static readonly AgentAssetLocation Standard = new(
        AgentAssetKind.Skill,
        "standard",
        AgentCommandStrings.SkillLocation_StandardName,
        AgentCommandStrings.SkillLocation_StandardDescription,
        Path.Combine(".agents", "skills"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User);

    /// <summary>
    /// Claude Code <c>.claude/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ClaudeCode = new(
        AgentAssetKind.Skill,
        "claudecode",
        AgentCommandStrings.SkillLocation_ClaudeCodeName,
        AgentCommandStrings.SkillLocation_ClaudeCodeDescription,
        Path.Combine(".claude", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// VS Code / GitHub Copilot <c>.github/skills/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation GitHubSkills = new(
        AgentAssetKind.Skill,
        "github",
        AgentCommandStrings.SkillLocation_GitHubSkillsName,
        AgentCommandStrings.SkillLocation_GitHubSkillsDescription,
        Path.Combine(".github", "skills"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// OpenCode <c>.opencode/skill/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation OpenCode = new(
        AgentAssetKind.Skill,
        "opencode",
        AgentCommandStrings.SkillLocation_OpenCodeName,
        AgentCommandStrings.SkillLocation_OpenCodeDescription,
        Path.Combine(".opencode", "skill"),
        isDefault: false,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// Project-level <c>.github/extensions/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation ProjectExtensions = new(
        AgentAssetKind.Extension,
        "project",
        AgentCommandStrings.ExtensionLocation_ProjectName,
        AgentCommandStrings.ExtensionLocation_ProjectDescription,
        Path.Combine(".github", "extensions"),
        isDefault: true,
        scopes: AgentAssetLocationScope.Workspace);

    /// <summary>
    /// User-level <c>~/.copilot/extensions/</c> location.
    /// </summary>
    public static readonly AgentAssetLocation UserExtensions = new(
        AgentAssetKind.Extension,
        "user",
        AgentCommandStrings.ExtensionLocation_UserName,
        AgentCommandStrings.ExtensionLocation_UserDescription,
        Path.Combine(".copilot", "extensions"),
        isDefault: false,
        scopes: AgentAssetLocationScope.User);

    private AgentAssetLocation(
        AgentAssetKind assetKind,
        string id,
        string displayName,
        string description,
        string relativeAssetDirectory,
        bool isDefault,
        AgentAssetLocationScope scopes)
    {
        AssetKind = assetKind;
        Id = id;
        DisplayName = displayName;
        Description = description;
        RelativeAssetDirectory = relativeAssetDirectory;
        IsDefault = isDefault;
        Scopes = scopes;
    }

    /// <summary>
    /// Gets the kind of agent asset installed at this location.
    /// </summary>
    public AgentAssetKind AssetKind { get; }

    /// <summary>
    /// Gets the non-localized identifier for this location, used for CLI option matching.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the display name for this location.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the description shown alongside the name in prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the relative asset directory path (e.g., ".agents/skills").
    /// </summary>
    public string RelativeAssetDirectory { get; }

    /// <summary>
    /// Gets whether this location should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the scopes where this agent asset location is installed.
    /// </summary>
    public AgentAssetLocationScope Scopes { get; }

    /// <summary>
    /// Gets all available agent asset locations.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> All { get; } =
        [Standard, ClaudeCode, GitHubSkills, OpenCode, ProjectExtensions, UserExtensions];

    /// <summary>
    /// Gets the locations available for the specified asset kind.
    /// </summary>
    public static IReadOnlyList<AgentAssetLocation> GetLocations(AgentAssetKind assetKind)
        => All.Where(location => location.AssetKind == assetKind).ToList();

    internal AgentAssetInstallTarget ResolveUserInstallTarget(DirectoryInfo homeDirectory, IEnvironment environment)
    {
        if (!Scopes.HasFlag(AgentAssetLocationScope.User))
        {
            throw new InvalidOperationException($"Agent asset location '{Id}' does not support user-level installation.");
        }

        if (this == UserExtensions)
        {
            var (rootDirectory, relativePath, usesConfiguredHome) =
                CopilotCliPaths.ResolveUserPath(homeDirectory, environment, "extensions");
            var displayDirectory = usesConfiguredHome
                ? Path.Combine(rootDirectory.FullName, relativePath)
                : GetUserDisplayDirectory(RelativeAssetDirectory);
            return new(rootDirectory, relativePath, displayDirectory);
        }

        return new(homeDirectory, RelativeAssetDirectory, GetUserDisplayDirectory(RelativeAssetDirectory));
    }

    /// <inheritdoc />
    public override string ToString() => Id;

    private static string GetUserDisplayDirectory(string relativeAssetDirectory)
    {
        var displayRelativeAssetDirectory = relativeAssetDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return $"~/{displayRelativeAssetDirectory}";
    }
}

internal readonly record struct AgentAssetInstallTarget(
    DirectoryInfo RootDirectory,
    string RelativeAssetDirectory,
    string DisplayDirectory);

/// <summary>
/// Identifies where an agent asset location is rooted.
/// </summary>
[Flags]
internal enum AgentAssetLocationScope
{
    /// <summary>
    /// No location scope.
    /// </summary>
    None = 0,

    /// <summary>
    /// The current workspace.
    /// </summary>
    Workspace = 1,

    /// <summary>
    /// The current user's home directory.
    /// </summary>
    User = 2,
}
