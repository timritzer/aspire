// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes agent environment configuration for detected agents.
/// This is the new command under 'aspire agent init'.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand
{
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly IAspireSkillsInstaller _aspireSkillsInstaller;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly IGitRepository _gitRepository;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly ITelemetryHookConfigurator _telemetryHookConfigurator;
    private readonly IEnvironment _environment;

    public AgentInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IAspireSkillsInstaller aspireSkillsInstaller,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        ITelemetryHookConfigurator telemetryHookConfigurator,
        IEnvironment environment,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _aspireSkillsInstaller = aspireSkillsInstaller;
        _playwrightCliInstaller = playwrightCliInstaller;
        _gitRepository = gitRepository;
        _languageDiscovery = languageDiscovery;
        _telemetryHookConfigurator = telemetryHookConfigurator;
        _environment = environment;

        Options.Add(s_workspaceRootOption);
        Options.Add(s_skillLocationsOption);
        Options.Add(s_skillsOption);
        Options.Add(s_extensionLocationsOption);
        Options.Add(s_extensionsOption);
    }

    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<string?> s_skillLocationsOption = new("--skill-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.GetLocations(AgentAssetKind.Skill).Select(static location => location.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", AgentAssetDefinition.GetCliDefined(AgentAssetKind.Skill).Select(static asset => asset.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionLocationsOption = new("--extension-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_ExtensionLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.GetLocations(AgentAssetKind.Extension).Select(static location => location.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionsOption = new("--extensions")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_ExtensionsOptionDescription,
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    /// <summary>
    /// Public entry point for executing the init command.
    /// This allows McpInitCommand to delegate to this implementation.
    /// </summary>
    internal Task<CommandResult> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    /// <summary>
    /// Prompts the user to run agent init after a successful command, then chains into agent init if accepted.
    /// Used by commands (e.g. <c>aspire init</c>, <c>aspire new</c>) to offer agent init as a follow-up step.
    /// When <paramref name="selectByDefault"/> is <see langword="null"/> every bundle-sourced asset is
    /// pre-selected, which is what <c>aspire init</c> wants because aspireify is the natural follow-up.
    /// Other callers (e.g. <c>aspire new</c>) can pass a predicate to additionally filter out assets that
    /// don't fit their context (such as one-time setup assets after a template has already produced the AppHost).
    /// Callers that expose <c>--skill-locations</c> and <c>--skills</c> can pass
    /// <paramref name="skillLocationsBinding"/> and <paramref name="skillsBinding"/> so the chained
    /// execution reuses the same non-interactive selection semantics as standalone <c>aspire agent init</c>.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> extensionLocationsBinding,
        PromptBinding<string?> extensionsBinding,
        Func<AgentAssetDefinition, bool>? selectByDefault,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != CliExitCodes.Success)
        {
            return new(
                previousResultExitCode,
                new Dictionary<AgentAssetKind, IReadOnlyList<AgentAssetLocation>>(),
                new Dictionary<AgentAssetKind, IReadOnlyList<AgentAssetDefinition>>());
        }

        // Add a separating line between prompt and previous work in aspire new and aspire init.
        interactionService.DisplayEmptyLine();

        var runAgentInit = await interactionService.PromptConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            binding: agentInitBinding,
            cancellationToken: cancellationToken);

        if (runAgentInit)
        {
            return await ExecuteAgentInitAsync(
                workspaceRoot,
                CreateAgentAssetBindings(
                    skillLocationsBinding,
                    skillsBinding,
                    extensionLocationsBinding,
                    extensionsBinding),
                selectByDefault,
                cancellationToken);
        }

        return new(
            CliExitCodes.Success,
            new Dictionary<AgentAssetKind, IReadOnlyList<AgentAssetLocation>>(),
            new Dictionary<AgentAssetKind, IReadOnlyList<AgentAssetDefinition>>());
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        // Standalone `aspire agent init` is typically run against an existing project, so don't
        // pre-select one-time aspireify assets even though every other bundle asset is default-on.
        // Users can still opt into them from the relevant asset prompt or option.
        var skillLocationsBinding = PromptBinding.Create(parseResult, s_skillLocationsOption);
        var skillsBinding = PromptBinding.Create(parseResult, s_skillsOption);
        var extensionLocationsBinding = PromptBinding.Create(parseResult, s_extensionLocationsOption);
        var extensionsBinding = PromptBinding.Create(parseResult, s_extensionsOption);
        var result = await ExecuteAgentInitAsync(
            workspaceRoot,
            CreateAgentAssetBindings(
                skillLocationsBinding,
                skillsBinding,
                extensionLocationsBinding,
                extensionsBinding),
            ExcludeOneTimeSetupAssetsFromDefaults,
            cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

    /// <summary>
    /// Names of bundle assets that perform one-time workspace setup and should NOT be
    /// pre-selected after a workspace was just produced by a template flow such as
    /// <c>aspire new</c> or after standalone <c>aspire agent init</c> (typically run
    /// against an existing project).
    /// </summary>
    /// <remarks>
    /// This is the single source of truth the CLI consults when filtering bundle assets out
    /// of the auto-preselection set. All bundle assets are default-on, so if a bundle ships
    /// a new wiring or bootstrap-style asset that should NOT auto-run in an already-bootstrapped
    /// workspace, add its name here.
    /// </remarks>
    internal static readonly IReadOnlySet<string> s_oneTimeSetupAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CommonAgentApplicators.AspireifySkillName,
    };

    /// <summary>
    /// Default-asset predicate used by flows that do not want one-time setup assets
    /// pre-selected — namely <c>aspire new</c> (template already created the AppHost) and
    /// standalone <c>aspire agent init</c> (typically run against an existing project).
    /// Assets filtered here remain available to opt into from their prompt or option.
    /// </summary>
    internal static bool ExcludeOneTimeSetupAssetsFromDefaults(AgentAssetDefinition asset)
        => asset.IsDefault && !s_oneTimeSetupAssetNames.Contains(asset.Name);

    private async Task<DirectoryInfo> PromptForWorkspaceRootAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Try to discover the git repository root to use as the default workspace root
        var gitRoot = await _gitRepository.GetRootAsync(cancellationToken);
        var defaultWorkspaceRoot = gitRoot ?? ExecutionContext.WorkingDirectory;

        // Prompt the user for the workspace root
        var workspaceRootPath = await InteractionService.PromptForFilePathAsync(
            McpCommandStrings.InitCommand_WorkspaceRootPrompt,
            binding: PromptBinding.Create(parseResult, s_workspaceRootOption, defaultWorkspaceRoot.FullName),
            validator: path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return ValidationResult.Error(McpCommandStrings.InitCommand_WorkspaceRootRequired);
                }

                if (!Directory.Exists(path))
                {
                    return ValidationResult.Error(string.Format(CultureInfo.InvariantCulture, McpCommandStrings.InitCommand_WorkspaceRootNotFound, path));
                }

                return ValidationResult.Success();
            },
            directory: true,
            cancellationToken: cancellationToken);

        return new DirectoryInfo(workspaceRootPath);
    }

    private async Task<AgentInitExecutionResult> ExecuteAgentInitAsync(
        DirectoryInfo workspaceRoot,
        IReadOnlyDictionary<AgentAssetKind, (PromptBinding<string?> Locations, PromptBinding<string?> Assets)> assetBindings,
        Func<AgentAssetDefinition, bool>? selectByDefault,
        CancellationToken cancellationToken)
    {
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };

        var applicators = await InteractionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken),
            emoji: KnownEmojis.Robot);

        // Detect the AppHost language to determine which skills to offer.
        // When no language is detected (e.g., standalone `aspire agent init`), language-restricted skills are excluded.
        var detectedLanguage = await _languageDiscovery.DetectLanguageRecursiveAsync(workspaceRoot, cancellationToken);

        // Apply deprecated config migrations silently (these are fixes, not choices)
        var configUpdates = applicators.Where(a => a.PromptGroup == McpInitPromptGroup.ConfigUpdates).ToList();
        var userChoices = applicators.Where(a => a.PromptGroup != McpInitPromptGroup.ConfigUpdates).ToList();

        foreach (var update in configUpdates)
        {
            try
            {
                await update.ApplyAsync(cancellationToken);
                InteractionService.DisplayMessage(KnownEmojis.Wrench, update.Description);
            }
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
            }
        }

        // --- Phases 1 and 2: Select locations and assets for each declared asset kind ---
        // Keep the outer orchestration driven by AgentAssetKind so adding a kind cannot leave
        // agent init silently running only the skills pipeline. Each kind supplies its own
        // prompt bindings; only kinds backed by an Aspire-skills bundle use bundle resolution.
        var selectedLocationsByAssetKind = new Dictionary<AgentAssetKind, IReadOnlyList<AgentAssetLocation>>();
        var selectedAssetsByAssetKind = new Dictionary<AgentAssetKind, IReadOnlyList<AgentAssetDefinition>>();
        var bundlesByAssetKind = new Dictionary<AgentAssetKind, AspireSkillsBundle?>();
        AgentEnvironmentApplicator? combinedMcpApplicator = null;
        var mcpApplicators = userChoices.Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments).ToList();
        var hasSelectionErrors = false;

        foreach (var assetKind in Enum.GetValues<AgentAssetKind>())
        {
            var messages = GetAgentAssetMessages(assetKind);
            if (!assetBindings.TryGetValue(assetKind, out var bindings))
            {
                throw new InvalidOperationException($"Agent asset kind '{assetKind}' does not define command bindings.");
            }

            // Resolve both prompts before location selection so incomplete wiring for a newly
            // declared kind fails even when the invocation selects no locations.
            var availableLocations = AgentAssetLocation.GetLocations(assetKind);
            if (availableLocations.Count == 0)
            {
                throw new InvalidOperationException($"Agent asset kind '{assetKind}' does not define any installation locations.");
            }

            var hasExplicitSelection =
                PromptBinding.Resolve(bindings.Locations).WasProvided ||
                PromptBinding.Resolve(bindings.Assets).WasProvided;
            var hasCompatibleClient = context.DetectedClients.Any(client => client.Supports(assetKind));
            if (assetKind is not AgentAssetKind.Skill &&
                !hasExplicitSelection &&
                !hasCompatibleClient)
            {
                selectedLocationsByAssetKind.Add(assetKind, []);
                selectedAssetsByAssetKind.Add(assetKind, []);
                bundlesByAssetKind.Add(assetKind, null);
                continue;
            }

            var defaultLocationIds = string.Join(
                ",",
                availableLocations.Where(static location => location.IsDefault).Select(static location => location.Id));
            var locationsBindingWithDefault = bindings.Locations.WithDefault(defaultLocationIds);
            var selectedLocations = await InteractionService.PromptForSelectionsAsync(
                messages.LocationSelectionPrompt,
                availableLocations,
                loc => $"{loc.DisplayName} — {loc.Description}",
                preSelected: availableLocations.Where(static location => location.IsDefault),
                optional: true,
                binding: locationsBindingWithDefault,
                echoSelected: false,
                cancellationToken: cancellationToken);
            selectedLocationsByAssetKind.Add(assetKind, selectedLocations);

            if (selectedLocations.Count == 0)
            {
                selectedAssetsByAssetKind.Add(assetKind, []);
                bundlesByAssetKind.Add(assetKind, null);
                continue;
            }

            if (assetKind is AgentAssetKind.Extension &&
                hasExplicitSelection &&
                !hasCompatibleClient &&
                ExplicitAssetSelectionAllowsInstallation(bindings.Assets))
            {
                InteractionService.DisplayMessage(
                    KnownEmojis.Warning,
                    AgentCommandStrings.InitCommand_NoCompatibleClientForExplicitExtensions);
            }

            var cliDefinedAssets = AgentAssetDefinition.GetCliDefined(assetKind);
            IReadOnlyList<AgentAssetDefinition> availableAssets;
            AspireSkillsBundle? bundle = null;
            string? bundleInstallFailureMessage = null;
            if (ShouldSkipBundleCatalogResolution(bindings.Assets, cliDefinedAssets))
            {
                availableAssets = cliDefinedAssets
                    .Where(asset => asset.IsApplicableToLanguage(detectedLanguage))
                    .ToList();
            }
            else
            {
                (availableAssets, bundle, bundleInstallFailureMessage) = await ResolveAvailableAgentAssetsAsync(
                    assetKind,
                    cliDefinedAssets,
                    detectedLanguage,
                    cancellationToken);
            }

            // Keep prompts stable regardless of the corresponding Aspire-skills manifest order.
            availableAssets = [.. availableAssets.OrderBy(static asset => asset.Name, StringComparer.OrdinalIgnoreCase)];
            var assetChoices = new List<object>();
            assetChoices.AddRange(availableAssets);

            AgentEnvironmentApplicator? promptMcpApplicator = null;
            if (assetKind is AgentAssetKind.Skill && mcpApplicators.Count > 0)
            {
                promptMcpApplicator = new AgentEnvironmentApplicator(
                    AgentCommandStrings.InitCommand_ConfigureMcpServer,
                    async ct =>
                    {
                        foreach (var mcp in mcpApplicators)
                        {
                            await mcp.ApplyAsync(ct);
                            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, mcp.Description);
                        }
                    },
                    promptGroup: McpInitPromptGroup.AdditionalOptions);
                assetChoices.Add(promptMcpApplicator);
            }

            var preSelectedItems = new List<object>();
            var defaultAssets = availableAssets
                .Where(selectByDefault ?? (static asset => asset.IsDefault))
                .ToList();
            preSelectedItems.AddRange(defaultAssets);
            // MCP is intentionally NOT pre-selected

            var defaultAssetNames = string.Join(",", defaultAssets.Select(static asset => asset.Name));
            var assetsBindingWithDefault = bindings.Assets.WithDefault(defaultAssetNames);

            // If the bundle failed, surface that failure before rejecting an explicitly requested
            // bundle-only asset as an unknown selection.
            var explicitBundleFailure = false;
            if (bundleInstallFailureMessage is not null)
            {
                var (wasProvided, requestedAssets, _) = PromptBinding.Resolve(assetsBindingWithDefault);
                if (assetKind is not AgentAssetKind.Skill && hasExplicitSelection)
                {
                    // Skills preserve their legacy best-effort behavior, but a user who explicitly
                    // requested a peer asset kind must not receive a success result after nothing installed.
                    InteractionService.DisplayError(bundleInstallFailureMessage);
                    hasSelectionErrors = true;
                    explicitBundleFailure = true;
                }
                else if (wasProvided &&
                    requestedAssets is not null &&
                    HasUnknownBundleAssetCandidate(requestedAssets, availableAssets, cliDefinedAssets))
                {
                    InteractionService.DisplayError(bundleInstallFailureMessage);
                }
            }

            var (assetsWereProvided, _, _) = PromptBinding.Resolve(assetsBindingWithDefault);
            if (assetChoices.Count == 0 && (!assetsWereProvided || explicitBundleFailure))
            {
                selectedAssetsByAssetKind.Add(assetKind, []);
                bundlesByAssetKind.Add(assetKind, bundle);
                continue;
            }

            var selectedItems = await InteractionService.PromptForSelectionsAsync(
                messages.AssetSelectionPrompt,
                assetChoices,
                item => item switch
                {
                    AgentAssetDefinition asset => $"{asset.Name.EscapeMarkup()} — {SimplifyDescription(asset.Description).EscapeMarkup()}",
                    AgentEnvironmentApplicator app => $"[bold]{app.Description}[/] [dim]{AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments}[/]",
                    _ => item.ToString()!
                },
                preSelected: preSelectedItems,
                optional: true,
                binding: assetsBindingWithDefault,
                // The MCP applicator participates in the skills prompt for UX, but it is not an
                // agent asset and must not be addressable through the asset selection option.
                bindingChoices: availableAssets.Cast<object>(),
                echoSelected: false,
                cancellationToken: cancellationToken);

            if (promptMcpApplicator is not null && selectedItems.Contains(promptMcpApplicator))
            {
                combinedMcpApplicator = promptMcpApplicator;
            }
            selectedAssetsByAssetKind.Add(assetKind, selectedItems.OfType<AgentAssetDefinition>().ToList());
            bundlesByAssetKind.Add(assetKind, bundle);
        }

        // --- Phase 3: Apply every asset-kind × location × selection combination ---
        // Asset files are small, so sequential execution keeps error reporting deterministic.
        var hasErrors = hasSelectionErrors;

        foreach (var assetKind in Enum.GetValues<AgentAssetKind>())
        {
            var selectedLocations = selectedLocationsByAssetKind[assetKind];
            var selectedAssets = selectedAssetsByAssetKind[assetKind];
            var bundle = bundlesByAssetKind[assetKind];
            var installedAssets = new List<InstalledAgentAssetSummaryItem>();

            foreach (var location in selectedLocations)
            {
                if (assetKind is AgentAssetKind.Skill &&
                    location.Scopes.HasFlag(AgentAssetLocationScope.Workspace))
                {
                    context.AddSkillBaseDirectory(location.RelativeAssetDirectory);
                }

                foreach (var asset in selectedAssets)
                {
                    if (asset.AssetKind != assetKind)
                    {
                        throw new InvalidOperationException(
                            $"Selected agent asset '{asset.Name}' has kind '{asset.AssetKind}' instead of '{assetKind}'.");
                    }

                    // Playwright CLI is installed by PlaywrightCliInstaller rather than as static files.
                    if (!asset.HasInstallableFiles)
                    {
                        continue;
                    }

                    if (location.Scopes.HasFlag(AgentAssetLocationScope.Workspace))
                    {
                        var installResult = await InstallAgentAssetAsync(
                            assetKind,
                            workspaceRoot,
                            location.RelativeAssetDirectory,
                            GetDisplayAgentAssetDirectory(location.RelativeAssetDirectory),
                            asset,
                            bundle,
                            cancellationToken);
                        hasErrors |= !installResult.Succeeded;
                        if (installResult.UpdatedAsset is not null)
                        {
                            installedAssets.Add(installResult.UpdatedAsset);
                        }
                    }

                    if (location.Scopes.HasFlag(AgentAssetLocationScope.User))
                    {
                        var installTarget = location.ResolveUserInstallTarget(ExecutionContext.HomeDirectory, _environment);
                        var installResult = await InstallAgentAssetAsync(
                            assetKind,
                            installTarget.RootDirectory,
                            installTarget.RelativeAssetDirectory,
                            installTarget.DisplayDirectory,
                            asset,
                            bundle,
                            cancellationToken);
                        hasErrors |= !installResult.Succeeded;
                        if (installResult.UpdatedAsset is not null)
                        {
                            installedAssets.Add(installResult.UpdatedAsset);
                        }
                    }
                }
            }

            DisplayInstalledAgentAssetsSummary(assetKind, installedAssets);
        }

        // --- Phase 4: Handle Playwright CLI (installs binary + mirrors skill files to registered directories) ---
        var selectedSkillLocations = selectedLocationsByAssetKind[AgentAssetKind.Skill];
        var selectedSkills = selectedAssetsByAssetKind[AgentAssetKind.Skill];
        var selectedSkillDirs = selectedSkillLocations
            .Where(static location => location.Scopes.HasFlag(AgentAssetLocationScope.Workspace))
            .Select(static location => location.RelativeAssetDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedSkills.Contains(AgentAssetDefinition.PlaywrightCli) && selectedSkillLocations.Count > 0)
        {
            try
            {
                var (status, message) = await _playwrightCliInstaller.InstallAsync(workspaceRoot.FullName, selectedSkillDirs, cancellationToken);
                switch (status)
                {
                    case PlaywrightInstallStatus.Installed:
                        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, AgentCommandStrings.InitCommand_InstalledPlaywrightCli);
                        break;
                    case PlaywrightInstallStatus.InstalledWithWarnings:
                        InteractionService.DisplayMessage(KnownEmojis.Warning, message!);
                        break;
                    case PlaywrightInstallStatus.Failed:
                        InteractionService.DisplayError(message!);
                        hasErrors = true;
                        break;
                    case PlaywrightInstallStatus.Skipped:
                        // npm is not available — not an error, just informational.
                        InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_PlaywrightCliSkipped);
                        break;
                    default:
                        throw new UnreachableException($"Unexpected PlaywrightInstallStatus: {status}");
                }
            }
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
                hasErrors = true;
            }
        }

        // --- Phase 5: Apply MCP server configuration if selected ---
        if (combinedMcpApplicator is not null)
        {
            try
            {
                await combinedMcpApplicator.ApplyAsync(cancellationToken);
            }
            // InvalidOperationException is thrown by scanner-generated applicators
            // (e.g., MCP config writers) when the underlying operation fails.
            // JsonException as InnerException indicates a malformed config file
            // (e.g., invalid JSON in .copilot/mcp-config.json or .vscode/mcp.json).
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
                if (ex.InnerException is JsonException)
                {
                    InteractionService.DisplaySubtleMessage(
                        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, combinedMcpApplicator.Description));
                }
                hasErrors = true;
            }
        }

        // --- Phase 6: Install agent telemetry hooks (default-on, parity with azure-skills) ---
        // Hooks are installed for every detected, supported client. Whether telemetry is actually
        // transmitted stays gated by the single ASPIRE_CLI_TELEMETRY_OPTOUT opt-out, which both the
        // hook scripts and the `aspire agent telemetry` command path re-check at runtime.
        await ConfigureTelemetryHooksAsync(context, cancellationToken);

        if (hasErrors)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.ConfigurationCompletedWithErrors);
        }
        else
        {
            InteractionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
        }

        return new(
            hasErrors ? CliExitCodes.InvalidCommand : CliExitCodes.Success,
            selectedLocationsByAssetKind,
            selectedAssetsByAssetKind);
    }

    private static IReadOnlyDictionary<AgentAssetKind, (PromptBinding<string?> Locations, PromptBinding<string?> Assets)> CreateAgentAssetBindings(
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> extensionLocationsBinding,
        PromptBinding<string?> extensionsBinding)
    {
        return new Dictionary<AgentAssetKind, (PromptBinding<string?> Locations, PromptBinding<string?> Assets)>
        {
            [AgentAssetKind.Skill] = (skillLocationsBinding, skillsBinding),
            [AgentAssetKind.Extension] = (extensionLocationsBinding, extensionsBinding),
        };
    }

    private static (
        string LocationSelectionPrompt,
        string AssetSelectionPrompt,
        string InstallFailureMessage,
        string InstalledSummary,
        string InstalledAssetsSummary,
        string InstalledLocationsSummary) GetAgentAssetMessages(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => (
                AgentCommandStrings.InitCommand_SelectSkillLocations,
                AgentCommandStrings.InitCommand_SelectSkills,
                AgentCommandStrings.InitCommand_FailedToInstallSkill,
                AgentCommandStrings.InitCommand_InstalledSkillsSummary,
                AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
                AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations),
            AgentAssetKind.Extension => (
                AgentCommandStrings.InitCommand_SelectExtensionLocations,
                AgentCommandStrings.InitCommand_SelectExtensions,
                AgentCommandStrings.InitCommand_FailedToInstallExtension,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummary,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummaryExtensions,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummaryLocations),
            _ => throw new ArgumentOutOfRangeException(
                nameof(assetKind),
                assetKind,
                "Agent asset kind does not define command messages."),
        };
    }

    private async Task ConfigureTelemetryHooksAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        TelemetryHookConfigurationResult result;
        try
        {
            result = await _telemetryHookConfigurator.ConfigureAsync(context.DetectedClients, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hook installation is best-effort transparency tooling; never fail `agent init` over it.
            // This deliberately catches everything except cancellation: besides file IO failures, a
            // corrupted CLI build could surface a missing embedded hook script as an
            // InvalidOperationException, and that must not abort the whole command either.
            InteractionService.DisplaySubtleMessage(ex.Message);
            return;
        }

        if (result.ConfiguredClients.Count > 0)
        {
            var clientNames = string.Join(", ", result.ConfiguredClients.Select(static client => client.Name));
            InteractionService.DisplayMessage(
                KnownEmojis.BarChart,
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHooksInstalled, clientNames));
        }

        foreach (var skip in result.Skipped)
        {
            var clientName = skip.Client.Name;
            var message = skip.Reason switch
            {
                TelemetryHookSkipReason.MalformedConfig => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedMalformedConfig, clientName),
                TelemetryHookSkipReason.UnexpectedConfigShape => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedUnexpectedShape, clientName),
                _ => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookWriteFailed, clientName),
            };

            // Skips are surfaced to the user but never treated as command failures: a user-owned
            // config we can't safely modify must not break `agent init`.
            InteractionService.DisplaySubtleMessage(message);
        }
    }

    private async Task<(IReadOnlyList<AgentAssetDefinition> Assets, AspireSkillsBundle? Bundle, string? FailureMessage)> ResolveAvailableAgentAssetsAsync(
        AgentAssetKind assetKind,
        IReadOnlyList<AgentAssetDefinition> cliDefinedAssets,
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken)
    {
        var assets = new List<AgentAssetDefinition>();
        AspireSkillsBundle? bundle = null;
        string? failureMessage = null;

        var result = await _aspireSkillsInstaller.InstallAsync(assetKind, cancellationToken);
        if (result.Status is AspireSkillsInstallStatus.Installed)
        {
            bundle = result.Bundle ?? throw new InvalidOperationException("Aspire Skills bundle installer returned an installed result without a bundle.");
            if (bundle.AssetKind != assetKind)
            {
                throw new InvalidOperationException(
                    $"Aspire Skills bundle has kind '{bundle.AssetKind}' instead of requested kind '{assetKind}'.");
            }

            assets.AddRange(bundle
                .GetAssetDefinitions()
                .Where(asset => !IsCliDefinedAssetName(asset.Name, cliDefinedAssets)));
        }
        else if (result.Status is AspireSkillsInstallStatus.Failed)
        {
            failureMessage = result.Message;
        }

        // Bundle failures do not prevent CLI-defined assets from being offered.
        assets.AddRange(cliDefinedAssets);

        return (assets
            .Where(asset => asset.IsApplicableToLanguage(detectedLanguage))
            .ToList(), bundle, failureMessage);
    }

    private static bool HasUnknownBundleAssetCandidate(
        string requestedAssets,
        IReadOnlyList<AgentAssetDefinition> availableAssets,
        IReadOnlyList<AgentAssetDefinition> cliDefinedAssets)
    {
        // Tokens like "all" / "none" don't name assets, so the bundle-failure
        // diagnostic doesn't apply — let the normal validation path handle them.
        if (string.IsNullOrWhiteSpace(requestedAssets) ||
            string.Equals(requestedAssets, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedAssets, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requested = requestedAssets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in requested)
        {
            if (IsCliDefinedAssetName(name, cliDefinedAssets))
            {
                continue;
            }

            if (!availableAssets.Any(asset => asset.HasName(name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipBundleCatalogResolution(
        PromptBinding<string?> assetsBinding,
        IReadOnlyList<AgentAssetDefinition> cliDefinedAssets)
    {
        var (wasProvided, optionValue, _) = PromptBinding.Resolve(assetsBinding);
        if (!wasProvided)
        {
            return false;
        }

        return ShouldSkipBundleCatalogResolution(optionValue, cliDefinedAssets);
    }

    private static bool ExplicitAssetSelectionAllowsInstallation(PromptBinding<string?> assetsBinding)
    {
        var (wasProvided, value, _) = PromptBinding.Resolve(assetsBinding);
        return !wasProvided ||
            !string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipBundleCatalogResolution(
        string? value,
        IReadOnlyList<AgentAssetDefinition> cliDefinedAssets)
    {
        if (string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var selectedAssetNames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return selectedAssetNames.Length > 0 &&
               selectedAssetNames.All(name => IsCliDefinedAssetName(name, cliDefinedAssets));
    }

    private static bool IsCliDefinedAssetName(
        string name,
        IReadOnlyList<AgentAssetDefinition> cliDefinedAssets)
    {
        return cliDefinedAssets.Any(asset => asset.HasName(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts the single short sentence from a skill description so the selection prompt
    /// stays readable.
    /// </summary>
    /// <remarks>
    /// Bundle manifest descriptions can include a bold skill-type prefix followed by a
    /// short tagline and additional usage guidance, for example:
    ///   "**WORKFLOW SKILL** - Top-level router for Aspire 13.4 distributed apps. Detects the AppHost. USE FOR: ..."
    /// This trims the prefix and returns only the first sentence. Inputs without the prefix
    /// or sentence terminator are returned trimmed-but-otherwise-unchanged so CLI-defined
    /// short descriptions are preserved as-is.
    /// </remarks>
    internal static string SimplifyDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var simplified = description.Trim();

        // Strip the leading bold "TYPE SKILL" prefix when present, and only then strip the
        // separator characters that typically follow it. Gating the separator strip on the
        // prefix match avoids silently mutating descriptions that legitimately start with
        // a dash, em-dash, or colon (e.g. "-mode flag explained" or ":memo notes").
        var strippedBoldPrefix = false;
        if (simplified.StartsWith("**", StringComparison.Ordinal))
        {
            var endBold = simplified.IndexOf("**", 2, StringComparison.Ordinal);
            if (endBold > 0)
            {
                simplified = simplified[(endBold + 2)..].TrimStart();
                strippedBoldPrefix = true;
            }
        }

        if (strippedBoldPrefix)
        {
            // Separators that typically follow the bold prefix (" - ", " — ", " – ", ": ").
            while (simplified.Length > 0 && simplified[0] is '-' or '\u2013' or '\u2014' or ':')
            {
                simplified = simplified[1..].TrimStart();
            }
        }

        // Return up to and including the first sentence-ending punctuation followed by
        // whitespace or end-of-string. This avoids splitting on inline punctuation such
        // as "13.4" or "github.com" inside the first sentence.
        for (var i = 0; i < simplified.Length; i++)
        {
            if (simplified[i] is '.' or '!' or '?'
                && (i + 1 >= simplified.Length || char.IsWhiteSpace(simplified[i + 1])))
            {
                return simplified[..(i + 1)];
            }
        }

        return simplified;
    }

    /// <summary>
    /// Installs files for an agent asset at the specified location.
    /// </summary>
    private async Task<AgentAssetInstallResult> InstallAgentAssetAsync(
        AgentAssetKind assetKind,
        DirectoryInfo rootDirectory,
        string relativeAssetDirectory,
        string displayAssetDirectory,
        AgentAssetDefinition asset,
        AspireSkillsBundle? bundle,
        CancellationToken cancellationToken)
    {
        var relativeAssetPath = Path.Combine(relativeAssetDirectory, asset.Name);
        var fullAssetDirectoryPath = Path.Combine(rootDirectory.FullName, relativeAssetPath);

        try
        {
            var assetFiles = await GetAgentAssetFilesAsync(assetKind, asset, bundle, cancellationToken);
            var stagedFiles = new List<StagedAgentAssetFile>();
            string? stagingDirectoryPath = null;

            try
            {
                foreach (var assetFile in assetFiles)
                {
                    var fullPath = Path.Combine(rootDirectory.FullName, relativeAssetPath, assetFile.RelativePath);
                    var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException($"Agent asset file '{assetFile.RelativePath}' does not have a parent directory.");
                    ValidateSafeExistingAssetDirectory(rootDirectory.FullName, directory);

                    if (ValidateAgentAssetDestinationFile(fullPath))
                    {
                        var existingContent = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                        if (assetFile.ContentEquals(existingContent))
                        {
                            continue;
                        }
                    }

                    stagingDirectoryPath ??= CreateAgentAssetTransactionDirectory(
                        rootDirectory.FullName,
                        Path.GetDirectoryName(fullAssetDirectoryPath)
                            ?? throw new InvalidOperationException($"Agent asset directory '{fullAssetDirectoryPath}' does not have a parent directory."),
                        asset.Name,
                        "staging");
                    var stagedPath = Path.Combine(stagingDirectoryPath, assetFile.RelativePath);
                    var stagedDirectory = Path.GetDirectoryName(stagedPath)
                        ?? throw new InvalidOperationException($"Staged agent asset file '{assetFile.RelativePath}' does not have a parent directory.");
                    EnsureSafeAssetDirectory(stagingDirectoryPath, stagedDirectory);
                    await WriteStagedAgentAssetFileAsync(stagedPath, assetFile.Bytes, cancellationToken);
                    stagedFiles.Add(new(fullPath, stagedPath));
                }

                if (assetKind is AgentAssetKind.Extension)
                {
                    // Extension directories are package-owned executable trees. Remove files that
                    // disappeared from the manifest so an upgrade cannot leave a hybrid of old and
                    // new JavaScript/UI assets. Skills preserve their existing additive behavior.
                    foreach (var staleFile in GetStaleAgentAssetFiles(fullAssetDirectoryPath, assetFiles))
                    {
                        stagedFiles.Add(new(staleFile, StagedPath: null));
                    }
                }

                if (stagedFiles.Count == 0)
                {
                    return new(Succeeded: true, UpdatedAsset: null);
                }

                var backupDirectoryPath = CreateAgentAssetTransactionDirectory(
                    rootDirectory.FullName,
                    Path.GetDirectoryName(fullAssetDirectoryPath)
                        ?? throw new InvalidOperationException($"Agent asset directory '{fullAssetDirectoryPath}' does not have a parent directory."),
                    asset.Name,
                    "rollback");
                PublishStagedAgentAssetFiles(rootDirectory.FullName, stagedFiles, backupDirectoryPath);

                return new(Succeeded: true, new InstalledAgentAssetSummaryItem(asset.Name, displayAssetDirectory));
            }
            finally
            {
                DeleteAgentAssetTransactionDirectory(stagingDirectoryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            var errorMessage = string.Format(
                CultureInfo.CurrentCulture,
                GetAgentAssetMessages(assetKind).InstallFailureMessage,
                asset.Name,
                fullAssetDirectoryPath,
                ex.Message);
            InteractionService.DisplayError(errorMessage);
            return new(Succeeded: false, UpdatedAsset: null);
        }
    }

    private void DisplayInstalledAgentAssetsSummary(
        AgentAssetKind assetKind,
        IReadOnlyList<InstalledAgentAssetSummaryItem> installedAssets)
    {
        if (installedAssets.Count == 0)
        {
            return;
        }

        var assetNames = string.Join(", ", GetUniqueValues(installedAssets.Select(static installedAsset => installedAsset.AssetName)));
        var locations = string.Join(", ", GetUniqueValues(installedAssets.Select(static installedAsset => installedAsset.DisplayLocation)));
        var messages = GetAgentAssetMessages(assetKind);
        var message = string.Join(Environment.NewLine,
            messages.InstalledSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, messages.InstalledAssetsSummary, assetNames)}",
            $"  {string.Format(CultureInfo.CurrentCulture, messages.InstalledLocationsSummary, locations)}");

        InteractionService.DisplayMessage(KnownEmojis.Robot, message);
    }

    private static IReadOnlyList<string> GetUniqueValues(IEnumerable<string> values)
    {
        var uniqueValues = new List<string>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (seenValues.Add(value))
            {
                uniqueValues.Add(value);
            }
        }

        return uniqueValues;
    }

    private static string GetDisplayAgentAssetDirectory(string relativeAssetDirectory)
    {
        var displayRelativeAssetDirectory = relativeAssetDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return displayRelativeAssetDirectory;
    }

    private static void EnsureSafeAssetDirectory(string rootDirectoryPath, string directoryPath)
        => ValidateOrCreateSafeAssetDirectory(rootDirectoryPath, directoryPath, createMissing: true);

    private static void ValidateSafeExistingAssetDirectory(string rootDirectoryPath, string directoryPath)
        => ValidateOrCreateSafeAssetDirectory(rootDirectoryPath, directoryPath, createMissing: false);

    private static void ValidateOrCreateSafeAssetDirectory(
        string rootDirectoryPath,
        string directoryPath,
        bool createMissing)
    {
        var relativeDirectoryPath = Path.GetRelativePath(rootDirectoryPath, directoryPath);
        if (Path.IsPathRooted(relativeDirectoryPath) ||
            relativeDirectoryPath.Equals("..", StringComparison.Ordinal) ||
            relativeDirectoryPath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativeDirectoryPath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Agent asset destination '{directoryPath}' is outside the installation root.");
        }

        if (createMissing)
        {
            Directory.CreateDirectory(rootDirectoryPath);
        }

        var currentPath = rootDirectoryPath;
        foreach (var segment in relativeDirectoryPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (TryGetPathAttributes(currentPath, out var attributes))
            {
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException($"Agent asset destination directory '{currentPath}' is a symbolic link or reparse point.");
                }

                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new IOException($"Agent asset destination directory '{currentPath}' is a file.");
                }

                continue;
            }

            if (!createMissing)
            {
                return;
            }

            Directory.CreateDirectory(currentPath);
            if (!TryGetPathAttributes(currentPath, out attributes) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !attributes.HasFlag(FileAttributes.Directory))
            {
                throw new IOException($"Agent asset destination directory '{currentPath}' could not be created safely.");
            }
        }
    }

    private static bool ValidateAgentAssetDestinationFile(string destinationPath)
    {
        if (!TryGetPathAttributes(destinationPath, out var attributes))
        {
            return false;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"Agent asset destination '{destinationPath}' is a symbolic link or reparse point.");
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Agent asset destination '{destinationPath}' is a directory.");
        }

        return true;
    }

    private static string CreateAgentAssetTransactionDirectory(
        string rootDirectoryPath,
        string parentDirectoryPath,
        string assetName,
        string purpose)
    {
        EnsureSafeAssetDirectory(rootDirectoryPath, parentDirectoryPath);

        // Staging and rollback directories must share the destination volume so publishing can
        // use rename operations rather than copying bytes through the destination path.
        var transactionDirectoryPath = Path.Combine(
            parentDirectoryPath,
            $".{assetName}.{purpose}.{Guid.NewGuid():N}");
        if (TryGetPathAttributes(transactionDirectoryPath, out _))
        {
            throw new IOException($"Agent asset transaction directory '{transactionDirectoryPath}' already exists.");
        }

        Directory.CreateDirectory(transactionDirectoryPath);
        if (!TryGetPathAttributes(transactionDirectoryPath, out var attributes) ||
            attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Agent asset transaction directory '{transactionDirectoryPath}' could not be created safely.");
        }

        return transactionDirectoryPath;
    }

    private static async Task WriteStagedAgentAssetFileAsync(
        string stagedPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            stagedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void PublishStagedAgentAssetFiles(
        string rootDirectoryPath,
        IReadOnlyList<StagedAgentAssetFile> stagedFiles,
        string backupDirectoryPath)
    {
        var publishedFiles = new List<PublishedAgentAssetFile>();

        try
        {
            for (var i = 0; i < stagedFiles.Count; i++)
            {
                var stagedFile = stagedFiles[i];
                var destinationDirectory = Path.GetDirectoryName(stagedFile.DestinationPath)
                    ?? throw new InvalidOperationException($"Agent asset destination '{stagedFile.DestinationPath}' does not have a parent directory.");
                EnsureSafeAssetDirectory(rootDirectoryPath, destinationDirectory);

                var destinationExists = ValidateAgentAssetDestinationFile(stagedFile.DestinationPath);
                if (destinationExists)
                {
                    var backupPath = Path.Combine(backupDirectoryPath, i.ToString(CultureInfo.InvariantCulture));
                    File.Move(stagedFile.DestinationPath, backupPath);
                    publishedFiles.Add(new(stagedFile.DestinationPath, backupPath));
                }

                if (stagedFile.StagedPath is not null)
                {
                    File.Move(stagedFile.StagedPath, stagedFile.DestinationPath);
                    if (!destinationExists)
                    {
                        publishedFiles.Add(new(stagedFile.DestinationPath, BackupPath: null));
                    }
                }
            }

        }
        catch (Exception publishException) when (publishException is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollbackExceptions = RollbackPublishedAgentAssetFiles(rootDirectoryPath, publishedFiles);
            if (rollbackExceptions.Count > 0)
            {
                throw new AggregateException(
                    $"Agent asset publication failed and rollback was incomplete. Original files were preserved under '{backupDirectoryPath}'.",
                    [publishException, .. rollbackExceptions]);
            }

            DeleteAgentAssetTransactionDirectory(backupDirectoryPath);
            throw;
        }

        DeleteAgentAssetTransactionDirectory(backupDirectoryPath);
    }

    private static IReadOnlyList<Exception> RollbackPublishedAgentAssetFiles(
        string rootDirectoryPath,
        IReadOnlyList<PublishedAgentAssetFile> publishedFiles)
    {
        var rollbackExceptions = new List<Exception>();
        for (var i = publishedFiles.Count - 1; i >= 0; i--)
        {
            var publishedFile = publishedFiles[i];
            try
            {
                var destinationDirectory = Path.GetDirectoryName(publishedFile.DestinationPath)
                    ?? throw new InvalidOperationException($"Agent asset destination '{publishedFile.DestinationPath}' does not have a parent directory.");
                EnsureSafeAssetDirectory(rootDirectoryPath, destinationDirectory);

                if (TryGetPathAttributes(publishedFile.DestinationPath, out var attributes))
                {
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        throw new IOException($"Agent asset rollback destination '{publishedFile.DestinationPath}' is a directory.");
                    }

                    // File.Delete removes a final-component symbolic link rather than following it.
                    File.Delete(publishedFile.DestinationPath);
                }

                if (publishedFile.BackupPath is not null)
                {
                    File.Move(publishedFile.BackupPath, publishedFile.DestinationPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                rollbackExceptions.Add(ex);
            }
        }

        return rollbackExceptions;
    }

    private IReadOnlyList<string> GetStaleAgentAssetFiles(
        string assetDirectoryPath,
        IReadOnlyList<AgentAssetFile> expectedFiles)
    {
        if (!TryGetPathAttributes(assetDirectoryPath, out var attributes))
        {
            return [];
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"Agent asset directory '{assetDirectoryPath}' is a symbolic link or reparse point.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"Agent asset directory '{assetDirectoryPath}' is a file.");
        }

        var expectedPaths = expectedFiles
            .Select(file => Path.GetFullPath(Path.Combine(assetDirectoryPath, file.RelativePath)))
            .ToHashSet(_environment.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var staleFiles = new List<string>();
        CollectStaleAgentAssetFiles(assetDirectoryPath, expectedPaths, staleFiles);
        return staleFiles;
    }

    private static void CollectStaleAgentAssetFiles(
        string directoryPath,
        IReadOnlySet<string> expectedPaths,
        List<string> staleFiles)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            var attributes = File.GetAttributes(entryPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Agent asset entry '{entryPath}' is a symbolic link or reparse point.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                CollectStaleAgentAssetFiles(entryPath, expectedPaths, staleFiles);
            }
            else if (!expectedPaths.Contains(Path.GetFullPath(entryPath)))
            {
                staleFiles.Add(entryPath);
            }
        }
    }

    private static void DeleteAgentAssetTransactionDirectory(string? transactionDirectoryPath)
    {
        if (transactionDirectoryPath is null ||
            !TryGetPathAttributes(transactionDirectoryPath, out var attributes))
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException($"Agent asset transaction directory '{transactionDirectoryPath}' changed unexpectedly.");
        }

        Directory.Delete(transactionDirectoryPath, recursive: true);
    }

    private static bool TryGetPathAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static Task<IReadOnlyList<AgentAssetFile>> GetAgentAssetFilesAsync(
        AgentAssetKind assetKind,
        AgentAssetDefinition asset,
        AspireSkillsBundle? bundle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (asset.Files.Count > 0)
        {
            return Task.FromResult<IReadOnlyList<AgentAssetFile>>(
                asset.Files
                    .Where(file => asset.ShouldInstallFile(file.RelativePath))
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .ToList());
        }

        if (bundle is not null)
        {
            return bundle.GetAssetFilesAsync(asset, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Agent asset '{asset.Name}' does not expose files for direct installation and no {assetKind} bundle was resolved.");
    }

    private sealed record InstalledAgentAssetSummaryItem(string AssetName, string DisplayLocation);

    private sealed record StagedAgentAssetFile(string DestinationPath, string? StagedPath);

    private sealed record PublishedAgentAssetFile(string DestinationPath, string? BackupPath);

    private readonly record struct AgentAssetInstallResult(bool Succeeded, InstalledAgentAssetSummaryItem? UpdatedAsset);
}

/// <summary>
/// Describes the result of running agent initialization.
/// </summary>
internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyDictionary<AgentAssetKind, IReadOnlyList<AgentAssetLocation>> LocationsByAssetKind,
    IReadOnlyDictionary<AgentAssetKind, IReadOnlyList<AgentAssetDefinition>> AssetsByAssetKind)
{
    /// <summary>
    /// Gets the selected locations for an asset kind.
    /// </summary>
    public IReadOnlyList<AgentAssetLocation> GetLocations(AgentAssetKind assetKind)
        => LocationsByAssetKind.TryGetValue(assetKind, out var locations) ? locations : [];

    /// <summary>
    /// Gets the selected assets for an asset kind.
    /// </summary>
    public IReadOnlyList<AgentAssetDefinition> GetAssets(AgentAssetKind assetKind)
        => AssetsByAssetKind.TryGetValue(assetKind, out var assets) ? assets : [];
}
