// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001, ASPIREENVIRONMENT001, ASPIREPIPELINES001

using System.Globalization;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures coordinated initial builds for path-based .NET resources.
/// </summary>
internal static class DotnetProjectBuildCoordinator
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CoordinatorState> s_states = new();

    internal const string BuildResourceName = "__dotnet-project-build";

    public static CoordinatorState? Prepare(
        IDistributedApplicationBuilder builder,
        DotnetProjectMetadata projectMetadata)
    {
        if (!builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        var state = s_states.GetValue(builder, static builder => new CoordinatorState(builder));
        if (IsProjectFile(projectMetadata.ProjectPath))
        {
            projectMetadata.SuppressBuild = true;
            projectMetadata.SetProjectPath(state.AddProject(projectMetadata.ProjectPath, projectMetadata.BuildConfiguration));
        }

        return state;
    }

    public static void Configure(
        IResourceBuilder<DotnetProjectResource> resourceBuilder,
        CoordinatorState? state)
    {
        if (state is null)
        {
            return;
        }

        var launchProfileEnvironment = resourceBuilder.Resource.GetEffectiveLaunchProfile()?.LaunchProfile.EnvironmentVariables;
        state.AddResource(
            resourceBuilder.Resource,
            launchProfileEnvironment is null
                ? []
                : new Dictionary<string, string>(launchProfileEnvironment, StringComparer.Ordinal));

        // Preserve the eagerly visible dependency used by model tests and tooling. BeforeStart replaces
        // the build plan after all resource environment callbacks and SDK roots are known, then adds the
        // final build barrier as an additional dependency.
        state.AddEagerBuildDependencies();
    }

    private static DotnetProjectBuildResource AddBuildResource(
        IDistributedApplicationBuilder builder,
        string name,
        string? configuration)
    {
        var buildDirectory = Path.Combine(builder.AppHostDirectory, ".aspire", "build");
        var buildResource = new DotnetProjectBuildResource(
            name,
            builder.AppHostDirectory,
            buildDirectory,
            TimeProvider.System);
        buildResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        builder.AddResource(buildResource)
            .WithArgs(async context =>
            {
                var buildTargetPath = await buildResource.GetBuildTargetPathAsync(
                    context.Logger,
                    context.CancellationToken).ConfigureAwait(false);

                context.Args.Add("build");
                context.Args.Add(buildTargetPath);

                if (!string.IsNullOrEmpty(configuration))
                {
                    context.Args.Add("--configuration");
                    context.Args.Add(configuration);
                }
            })
            .WithIconName("CodeCsRectangle")
            .ExcludeFromManifest()
            .WithHiddenOnCompletion(0);

        return buildResource;
    }

    private static Action? AddBuildDependency(
        IDistributedApplicationBuilder builder,
        DotnetProjectResource resource,
        DotnetProjectBuildResource buildResource)
    {
        if (resource.Annotations.OfType<WaitAnnotation>().Any(
            annotation => annotation.WaitType is WaitType.WaitForCompletion &&
                          ReferenceEquals(annotation.Resource, buildResource)))
        {
            return null;
        }

        var existingAnnotations = resource.Annotations.ToHashSet(ReferenceEqualityComparer.Instance);
        builder.CreateResourceBuilder(resource)
            .WaitForCompletion(builder.CreateResourceBuilder(buildResource));
        var addedAnnotations = resource.Annotations
            .Where(annotation => !existingAnnotations.Contains(annotation))
            .ToArray();
        var subscription = builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            (@event, cancellationToken) =>
                WaitForSuccessfulBuildAsync(@event.Services, buildResource, cancellationToken));

        return () =>
        {
            builder.Eventing.Unsubscribe(subscription);
            foreach (var annotation in addedAnnotations)
            {
                resource.Annotations.Remove(annotation);
            }
        };
    }

    private static Action? AddBuildDependency(
        IDistributedApplicationBuilder builder,
        DotnetProjectBuildResource resource,
        DotnetProjectBuildResource dependency)
    {
        var existingAnnotations = resource.Annotations.ToHashSet(ReferenceEqualityComparer.Instance);
        builder.CreateResourceBuilder(resource)
            .WaitForCompletion(builder.CreateResourceBuilder(dependency));
        var addedAnnotations = resource.Annotations
            .Where(annotation => !existingAnnotations.Contains(annotation))
            .ToArray();
        var subscription = builder.Eventing.Subscribe<BeforeResourceStartedEvent>(
            resource,
            (@event, cancellationToken) =>
                WaitForSuccessfulBuildAsync(@event.Services, dependency, cancellationToken));

        return () =>
        {
            builder.Eventing.Unsubscribe(subscription);
            foreach (var annotation in addedAnnotations)
            {
                resource.Annotations.Remove(annotation);
            }
        };
    }

    private static async Task WaitForSuccessfulBuildAsync(
        IServiceProvider services,
        DotnetProjectBuildResource buildResource,
        CancellationToken cancellationToken)
    {
        var notificationService = services.GetRequiredService<ResourceNotificationService>();
        var buildEvent = await notificationService.WaitForResourceAsync(
            buildResource.Name,
            resourceEvent => IsSettledBuildSnapshot(resourceEvent.Snapshot),
            cancellationToken).ConfigureAwait(false);

        if (buildEvent.Snapshot.State?.Text == KnownResourceStates.FailedToStart ||
            buildEvent.Snapshot.ExitCode is not 0)
        {
            var exitCode = buildEvent.Snapshot.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
            throw new DistributedApplicationException(
                $"The coordinated .NET project build failed with exit code {exitCode}. See resource '{buildResource.Name}' for build output.");
        }
    }

    internal static bool IsSettledBuildSnapshot(CustomResourceSnapshot snapshot) =>
        snapshot.State?.Text == KnownResourceStates.FailedToStart ||
        (KnownResourceStates.TerminalStates.Contains(snapshot.State?.Text) &&
         snapshot.ExitCode is not null);

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedPath(string path) =>
        IsProjectFile(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    internal sealed class CoordinatorState : IDisposable
    {
        private readonly IDistributedApplicationBuilder _builder;
        private readonly List<ResourceRegistration> _registrations = [];
        private readonly List<DotnetProjectBuildResource> _ownedBuildResources = [];
        private readonly Dictionary<DotnetProjectResource, Action> _eagerDependencyRollbacks =
            new(ReferenceEqualityComparer.Instance);
        private bool _materialized;
        private bool _disposed;

        public CoordinatorState(IDistributedApplicationBuilder builder)
        {
            _builder = builder;
            builder.Services.AddSingleton(_ => this);
            builder.Pipeline.WithFinalAction(
                WellKnownPipelineSteps.BeforeStart,
                stepContext => stepContext.Services
                    .GetRequiredService<CoordinatorState>()
                    .MaterializeBuildPlan(stepContext.Model, stepContext.Services));
        }

        public IReadOnlyList<DotnetProjectResource> Resources =>
            _registrations.Select(registration => registration.Resource).ToArray();

        public DotnetProjectBuildResource? PrimaryBuildResource { get; private set; }

        public string AddProject(string projectPath, string? configuration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            PrimaryBuildResource ??= CreateBuildResource(configuration);
            return PrimaryBuildResource.AddProject(projectPath);
        }

        public void AddResource(
            DotnetProjectResource resource,
            IReadOnlyDictionary<string, string> launchProfileEnvironment)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var baselineEnvironmentCallbacks = resource.TryGetEnvironmentVariables(out var environmentCallbacks)
                ? new HashSet<EnvironmentCallbackAnnotation>(environmentCallbacks, ReferenceEqualityComparer.Instance)
                : [];
            _registrations.Add(new ResourceRegistration(
                resource,
                baselineEnvironmentCallbacks,
                launchProfileEnvironment));
        }

        public void AddEagerBuildDependencies()
        {
            if (PrimaryBuildResource is not { } primaryBuildResource)
            {
                return;
            }

            foreach (var registration in _registrations)
            {
                var resource = registration.Resource;
                if (_eagerDependencyRollbacks.ContainsKey(resource) ||
                    resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is not { } metadata ||
                    !IsSupportedPath(metadata.ProjectPath))
                {
                    continue;
                }

                if (AddBuildDependency(_builder, resource, primaryBuildResource) is { } rollback)
                {
                    _eagerDependencyRollbacks.Add(resource, rollback);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RemoveEagerBuildDependencies(_registrations.Select(registration => registration.Resource));
            foreach (var buildResource in _ownedBuildResources)
            {
                buildResource.Dispose();
            }
        }

        internal Task MaterializeBuildPlan(
            DistributedApplicationModel model,
            IServiceProvider services)
        {
            if (_materialized)
            {
                return Task.CompletedTask;
            }

            var activeResources = model.Resources.ToHashSet(ReferenceEqualityComparer.Instance);
            var activeRegistrations = _registrations
                .Where(registration => activeResources.Contains(registration.Resource))
                .ToArray();
            RemoveEagerBuildDependencies(
                _registrations
                    .Where(registration => !activeResources.Contains(registration.Resource))
                    .Select(registration => registration.Resource));

            var projectEntries = activeRegistrations
                .Select(registration => new ProjectEntry(
                    registration,
                    registration.Resource.Annotations.OfType<DotnetProjectMetadata>().Single()))
                .Where(entry => IsProjectFile(entry.Metadata.ProjectPath))
                .ToArray();
            var missingProjectEntries = projectEntries
                .Where(entry => !File.Exists(entry.Metadata.ProjectPath))
                .ToArray();
            foreach (var missingEntry in missingProjectEntries)
            {
                // Missing project files intentionally remain on the ordinary resource-start path so the
                // resulting dotnet error names only that resource instead of failing the shared build.
                missingEntry.Metadata.SuppressBuild = false;
                RemoveEagerBuildDependencies([missingEntry.Registration.Resource]);
            }
            projectEntries = projectEntries
                .Where(entry => File.Exists(entry.Metadata.ProjectPath))
                .ToArray();

            if (projectEntries.Length == 0)
            {
                RemoveEagerBuildDependencies(activeRegistrations.Select(registration => registration.Resource));
                if (PrimaryBuildResource is { } unusedBuildResource)
                {
                    _builder.Resources.Remove(unusedBuildResource);
                    _ownedBuildResources.Remove(unusedBuildResource);
                    unusedBuildResource.Dispose();
                    PrimaryBuildResource = null;
                }

                _materialized = true;
                return Task.CompletedTask;
            }

            var buildSteps = CreateBuildSteps(projectEntries);
            var applicationLifetime = services.GetRequiredService<IHostApplicationLifetime>();
            var primaryBuildResource = PrimaryBuildResource!;
            var originalPrimaryProjectPaths = primaryBuildResource.ProjectPaths;
            var originalPrimaryWorkingDirectory = primaryBuildResource.WorkingDirectory;
            var rollbackActions = new Stack<Action>();

            try
            {
                rollbackActions.Push(() =>
                    primaryBuildResource.ConfigureTraversalBuild(
                        originalPrimaryProjectPaths,
                        originalPrimaryWorkingDirectory));

                var buildResources = new List<DotnetProjectBuildResource>(buildSteps.Count);
                for (var index = 0; index < buildSteps.Count; index++)
                {
                    var step = buildSteps[index];
                    var buildResource = index == 0
                        ? primaryBuildResource
                        : CreateBuildResource(step.Configuration, index + 1);

                    if (index > 0)
                    {
                        rollbackActions.Push(() =>
                        {
                            _builder.Resources.Remove(buildResource);
                            _ownedBuildResources.Remove(buildResource);
                            buildResource.Dispose();
                        });
                    }

                    if (step.IsTraversal)
                    {
                        buildResource.ConfigureTraversalBuild(
                            step.Projects.Select(entry => entry.Metadata.ProjectPath),
                            step.WorkingDirectory);
                    }
                    else
                    {
                        var entry = step.Projects.Single();
                        buildResource.ConfigureDirectBuild(entry.Metadata.ProjectPath, step.WorkingDirectory);
                        var environmentAnnotation = CopyProjectEnvironment(entry, buildResource);
                        rollbackActions.Push(() => buildResource.Annotations.Remove(environmentAnnotation));
                        if (FindRebuilder(model, entry.Registration.Resource) is { } rebuilder)
                        {
                            var rebuildEnvironmentAnnotation = CopyProjectEnvironment(entry, rebuilder);
                            rollbackActions.Push(() => rebuilder.Annotations.Remove(rebuildEnvironmentAnnotation));
                        }
                    }

                    buildResource.RegisterForShutdown(applicationLifetime);
                    buildResources.Add(buildResource);
                }

                for (var index = 1; index < buildResources.Count; index++)
                {
                    if (AddBuildDependency(_builder, buildResources[index], buildResources[index - 1]) is { } rollback)
                    {
                        rollbackActions.Push(rollback);
                    }
                }

                var finalBuildResource = buildResources[^1];
                foreach (var registration in activeRegistrations)
                {
                    var resource = registration.Resource;
                    if (resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is { } metadata &&
                        IsSupportedPath(metadata.ProjectPath) &&
                        (!IsProjectFile(metadata.ProjectPath) || File.Exists(metadata.ProjectPath)))
                    {
                        if (AddBuildDependency(_builder, resource, finalBuildResource) is { } rollback)
                        {
                            rollbackActions.Push(rollback);
                        }
                    }
                }

                _materialized = true;
            }
            catch (Exception materializationException)
            {
                var rollbackExceptions = new List<Exception>();
                while (rollbackActions.TryPop(out var rollback))
                {
                    try
                    {
                        rollback();
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackExceptions.Add(rollbackException);
                    }
                }

                if (rollbackExceptions.Count > 0)
                {
                    throw new AggregateException(
                        "Coordinated .NET project build-plan materialization failed and could not be fully rolled back.",
                        [materializationException, .. rollbackExceptions]);
                }

                throw;
            }

            return Task.CompletedTask;
        }

        private void RemoveEagerBuildDependencies(IEnumerable<DotnetProjectResource> resources)
        {
            foreach (var resource in resources)
            {
                if (_eagerDependencyRollbacks.Remove(resource, out var rollback))
                {
                    rollback();
                }
            }
        }

        private static List<BuildStep> CreateBuildSteps(IEnumerable<ProjectEntry> entries)
        {
            var entryList = entries.ToArray();
            var conflictingDuplicate = entryList
                .GroupBy(entry => entry.Metadata.ProjectPath, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1 && group.Any(RequiresContextSpecificBuild));
            if (conflictingDuplicate is not null)
            {
                var resourceNames = string.Join(
                    ", ",
                    conflictingDuplicate.Select(entry => $"'{entry.Registration.Resource.Name}'"));
                throw new DistributedApplicationException(
                    $"The .NET project '{conflictingDuplicate.Key}' is registered multiple times by resources {resourceNames}, " +
                    "and at least one registration has a project-specific build environment. The coordinated build cannot " +
                    "produce distinct outputs for the same project path.");
            }

            var steps = new List<BuildStep>();
            var traversalSteps = new Dictionary<BuildContext, BuildStep>();

            foreach (var entry in entryList)
            {
                if (RequiresContextSpecificBuild(entry))
                {
                    steps.Add(BuildStep.CreateDirect(entry));
                    continue;
                }

                var globalJsonPath = FindNearestGlobalJson(entry.Registration.Resource.WorkingDirectory);
                var context = new BuildContext(globalJsonPath, entry.Metadata.BuildConfiguration);
                if (!traversalSteps.TryGetValue(context, out var step))
                {
                    var workingDirectory = globalJsonPath is null
                        ? entry.Registration.Resource.WorkingDirectory
                        : Path.GetDirectoryName(globalJsonPath)!;
                    step = BuildStep.CreateTraversal(entry, workingDirectory);
                    traversalSteps.Add(context, step);
                    steps.Add(step);
                }
                else
                {
                    step.Projects.Add(entry);
                }
            }

            return steps;
        }

        private DotnetProjectBuildResource CreateBuildResource(string? configuration, int? ordinal = null)
        {
            var name = ordinal is null
                ? BuildResourceName
                : $"{BuildResourceName}-{ordinal.Value.ToString(CultureInfo.InvariantCulture)}";
            var buildResource = AddBuildResource(
                _builder,
                name,
                configuration);
            _ownedBuildResources.Add(buildResource);
            return buildResource;
        }

        private static EnvironmentCallbackAnnotation CopyProjectEnvironment(
            ProjectEntry entry,
            IResource buildResource)
        {
            // Build-plan materialization is the final action of BeforeStart, so this snapshot includes every
            // callback that can affect the coordinated initial build without admitting later runtime mutations.
            var buildEnvironmentCallbacks = GetContextSpecificBuildCallbacks(entry.Registration).ToArray();
            var annotation = new EnvironmentCallbackAnnotation(async context =>
            {
                foreach (var (name, value) in entry.Registration.LaunchProfileEnvironment)
                {
                    context.EnvironmentVariables.TryAdd(name, Environment.ExpandEnvironmentVariables(value));
                }

                var projectContext = new EnvironmentCallbackContext(
                    context.ExecutionContext,
                    entry.Registration.Resource,
                    context.EnvironmentVariables,
                    context.CancellationToken)
                {
                    Logger = context.Logger,
                };
                foreach (var callback in buildEnvironmentCallbacks)
                {
                    await callback.Callback(projectContext).ConfigureAwait(false);
                }

                entry.Metadata.SetBuildEnvironmentVariableNames(context.EnvironmentVariables.Keys);
            });
            buildResource.Annotations.Add(annotation);
            return annotation;
        }

        private static IResource? FindRebuilder(
            DistributedApplicationModel model,
            DotnetProjectResource resource) =>
            model.Resources
                .SingleOrDefault(candidate =>
                    candidate.Name == $"{resource.Name}-rebuilder" &&
                    candidate is IResourceWithParent<IResource> parent &&
                    ReferenceEquals(parent.Parent, resource));

        private static string? FindNearestGlobalJson(string workingDirectory)
        {
            var physicalWorkingDirectory = PathNormalizer.ResolveSymlinks(Path.GetFullPath(workingDirectory));
            for (var directory = new DirectoryInfo(physicalWorkingDirectory); directory is not null; directory = directory.Parent)
            {
                var globalJsonPath = Path.Combine(directory.FullName, "global.json");
                if (File.Exists(globalJsonPath))
                {
                    return PathNormalizer.ResolveToFilesystemPath(globalJsonPath);
                }
            }

            return null;
        }

        private static bool RequiresContextSpecificBuild(ProjectEntry entry)
        {
            if (entry.Registration.LaunchProfileEnvironment.Count > 0)
            {
                return true;
            }

            return GetContextSpecificBuildCallbacks(entry.Registration).Any();
        }

        private static IEnumerable<EnvironmentCallbackAnnotation> GetContextSpecificBuildCallbacks(
            ResourceRegistration registration) =>
            registration.Resource.TryGetEnvironmentVariables(out var environmentCallbacks)
                ? environmentCallbacks.Where(
                    callback => callback is not RuntimeEnvironmentCallbackAnnotation &&
                                !registration.BaselineEnvironmentCallbacks.Contains(callback))
                : [];

        private readonly record struct BuildContext(string? GlobalJsonPath, string? Configuration);

        private sealed record ProjectEntry(
            ResourceRegistration Registration,
            DotnetProjectMetadata Metadata);

        private sealed record ResourceRegistration(
            DotnetProjectResource Resource,
            HashSet<EnvironmentCallbackAnnotation> BaselineEnvironmentCallbacks,
            IReadOnlyDictionary<string, string> LaunchProfileEnvironment);

        private sealed class BuildStep
        {
            private BuildStep(
                bool isTraversal,
                string workingDirectory,
                string? configuration,
                List<ProjectEntry> projects)
            {
                IsTraversal = isTraversal;
                WorkingDirectory = workingDirectory;
                Configuration = configuration;
                Projects = projects;
            }

            public bool IsTraversal { get; }

            public string WorkingDirectory { get; }

            public string? Configuration { get; }

            public List<ProjectEntry> Projects { get; }

            public static BuildStep CreateTraversal(ProjectEntry entry, string workingDirectory) =>
                new(
                    isTraversal: true,
                    workingDirectory,
                    entry.Metadata.BuildConfiguration,
                    [entry]);

            public static BuildStep CreateDirect(ProjectEntry entry) =>
                new(
                    isTraversal: false,
                    entry.Registration.Resource.WorkingDirectory,
                    entry.Metadata.BuildConfiguration,
                    [entry]);
        }
    }
}
