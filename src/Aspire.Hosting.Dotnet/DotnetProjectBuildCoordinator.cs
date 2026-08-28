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
            var interleavedEntry = projectEntries
                .Where(HasInterleavedBuildAndRuntimeCallbacks)
                .FirstOrDefault();
            if (interleavedEntry is not null)
            {
                throw new DistributedApplicationException(
                    $"The .NET project resource '{interleavedEntry.Registration.Resource.Name}' has a build-affecting " +
                    "environment callback registered after a runtime-only callback. The coordinated build cannot " +
                    "preserve that callback ordering. Configure build-affecting environment variables before runtime " +
                    "references and runtime-only environment callbacks.");
            }

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
                        rollbackActions.Push(ValidateMaterializedBuildCallbacks(buildResource, step.Projects));
                    }
                    else
                    {
                        var entry = step.Projects.Single();
                        buildResource.ConfigureDirectBuild(entry.Metadata.ProjectPath, step.WorkingDirectory);

                        // One coordinator-owned evaluation feeds the coordinated build, the rebuilder, the IDE launch
                        // configuration, and the project itself, so the user callbacks run once and every consumer
                        // observes identical build variables.
                        var sharedBuildEnvironment = new SharedBuildEnvironment(entry);
                        if (sharedBuildEnvironment.Callbacks.Count == 0)
                        {
                            entry.Metadata.SetBuildEnvironmentVariableNames(entry.Registration.LaunchProfileEnvironment.Keys);
                        }
                        rollbackActions.Push(ReplaceProjectBuildCallbacks(sharedBuildEnvironment));
                        rollbackActions.Push(ValidateMaterializedBuildCallbacks(buildResource, step.Projects));
                        rollbackActions.Push(ApplyBuildEnvironment(buildResource, sharedBuildEnvironment));
                        if (FindRebuilder(model, entry.Registration.Resource) is { } rebuilder)
                        {
                            rollbackActions.Push(ValidateMaterializedBuildCallbacks(rebuilder, step.Projects));
                            rollbackActions.Push(ApplyBuildEnvironment(rebuilder, sharedBuildEnvironment));
                        }
                    }

                    foreach (var entry in step.Projects)
                    {
                        rollbackActions.Push(ValidateMaterializedBuildCallbacks(
                            entry.Registration.Resource,
                            [entry],
                            runtimeOnly: true));
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

        /// <summary>
        /// Copies the coordinated build environment onto a resource that performs the build (the coordinated build
        /// resource or the project rebuilder).
        /// </summary>
        private static Action ApplyBuildEnvironment(IResource target, SharedBuildEnvironment sharedBuildEnvironment)
        {
            var annotation = new EnvironmentCallbackAnnotation(sharedBuildEnvironment.ApplyBuildEnvironmentAsync);
            target.Annotations.Add(annotation);
            return () => target.Annotations.Remove(annotation);
        }

        private static Action ValidateMaterializedBuildCallbacks(
            IResource target,
            IEnumerable<ProjectEntry> entries,
            bool runtimeOnly = false)
        {
            var snapshots = entries
                .Select(entry => new BuildCallbackSnapshot(
                    entry,
                    GetContextSpecificBuildCallbacks(entry.Registration).ToArray()))
                .ToArray();
            void Validate(EnvironmentCallbackContext _)
            {
                foreach (var snapshot in snapshots)
                {
                    var currentCallbacks = GetContextSpecificBuildCallbacks(snapshot.Entry.Registration);
                    if (!currentCallbacks.SequenceEqual(
                        snapshot.Callbacks,
                        ReferenceEqualityComparer.Instance))
                    {
                        throw new DistributedApplicationException(
                            $"The build environment of .NET project resource '{snapshot.Entry.Registration.Resource.Name}' " +
                            "changed after the coordinated build plan was materialized. Configure build-affecting " +
                            "environment variables while constructing the AppHost or in a pipeline step required by " +
                            "BeforeStart; do not add or remove them after materialization.");
                    }
                }
            }

            EnvironmentCallbackAnnotation annotation = runtimeOnly
                ? new RuntimeEnvironmentCallbackAnnotation(Validate)
                : new EnvironmentCallbackAnnotation(Validate);
            target.Annotations.Add(annotation);

            return () => target.Annotations.Remove(annotation);
        }

        /// <summary>
        /// Replaces each build-relevant callback on the project with an in-place stand-in that replays what the
        /// coordinated evaluation recorded for that callback.
        /// </summary>
        /// <remarks>
        /// The stand-ins keep the positions of the callbacks they replace so callbacks registered around them - most
        /// importantly runtime-only callbacks - still observe the same values in the same order as before.
        /// </remarks>
        private static Action ReplaceProjectBuildCallbacks(SharedBuildEnvironment sharedBuildEnvironment)
        {
            var annotations = sharedBuildEnvironment.Resource.Annotations;

            // Resolve every position before mutating anything so a callback that disappeared cannot leave the project
            // with a partially replaced set of annotations.
            var positions = sharedBuildEnvironment.Callbacks
                .Select((original, ordinal) => (Original: original, Ordinal: ordinal, Index: annotations.IndexOf(original)))
                .ToArray();
            if (Array.FindIndex(positions, position => position.Index < 0) >= 0)
            {
                throw new DistributedApplicationException(
                    $"An environment callback of resource '{sharedBuildEnvironment.Resource.Name}' was removed while the " +
                    "coordinated .NET project build plan was being materialized.");
            }

            var replacements = new List<(EnvironmentCallbackAnnotation Original, EnvironmentCallbackAnnotation Replay)>(
                positions.Length);
            foreach (var (original, ordinal, index) in positions)
            {
                var replay = new EnvironmentCallbackAnnotation(
                    context => sharedBuildEnvironment.ApplyContributionAsync(ordinal, context));
                annotations[index] = replay;
                replacements.Add((original, replay));
            }

            return () =>
            {
                foreach (var (original, replay) in replacements)
                {
                    var index = annotations.IndexOf(replay);
                    if (index >= 0)
                    {
                        annotations[index] = original;
                    }
                }
            };
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

        private static bool HasInterleavedBuildAndRuntimeCallbacks(ProjectEntry entry)
        {
            var buildCallbacks = GetContextSpecificBuildCallbacks(entry.Registration)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            if (buildCallbacks.Count == 0 ||
                !entry.Registration.Resource.TryGetEnvironmentVariables(out var environmentCallbacks))
            {
                return false;
            }

            var encounteredRuntimeCallback = false;
            foreach (var callback in environmentCallbacks)
            {
                if (callback is RuntimeEnvironmentCallbackAnnotation)
                {
                    encounteredRuntimeCallback = true;
                }
                else if (encounteredRuntimeCallback && buildCallbacks.Contains(callback))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly record struct BuildContext(string? GlobalJsonPath, string? Configuration);

        /// <summary>
        /// Owns the single evaluation of a project's build-relevant environment callbacks.
        /// </summary>
        /// <remarks>
        /// The coordinated build resource, the project rebuilder, the IDE launch configuration, and the project itself
        /// must all see the same build variables. Letting each of them evaluate the user callbacks would run those
        /// callbacks several times and would let whichever resource evaluated first decide the build inputs. That is
        /// unsafe because the project's own environment also carries runtime-only variables (service discovery,
        /// connection strings, endpoint-derived values), which must never reach the build. Recording the result here
        /// rather than in the <see cref="EnvironmentCallbackAnnotation"/> cache also keeps ownership stable: DCP clears
        /// that cache whenever a resource restarts, so a restarted project would otherwise be able to re-run the user
        /// callbacks against its runtime-rich context and silently change the build inputs.
        /// </remarks>
        private sealed class SharedBuildEnvironment
        {
            private readonly ProjectEntry _entry;
            private readonly object _lock = new();
            private Task<BuildEnvironmentEvaluation>? _evaluation;

            public SharedBuildEnvironment(ProjectEntry entry)
            {
                _entry = entry;
                Callbacks = GetContextSpecificBuildCallbacks(entry.Registration).ToArray();
            }

            public DotnetProjectResource Resource => _entry.Registration.Resource;

            /// <summary>
            /// Gets the project callbacks that contribute to the coordinated build, in registration order.
            /// </summary>
            /// <remarks>
            /// The build resource validates this materialized snapshot before evaluating it, so a later mutation cannot
            /// silently produce output that differs from the project launch environment.
            /// </remarks>
            public IReadOnlyList<EnvironmentCallbackAnnotation> Callbacks { get; }

            /// <summary>
            /// Applies the complete coordinated build environment - launch profile values and every callback
            /// contribution - to the resource that runs the build.
            /// </summary>
            public async Task ApplyBuildEnvironmentAsync(EnvironmentCallbackContext context)
            {
                var evaluation = await EvaluateOnceAsync(context).ConfigureAwait(false);
                foreach (var (name, value) in evaluation.LaunchProfileEnvironment)
                {
                    // Launch profile values never overwrite what the build resource already carries, which matches how
                    // the project applies its own launch profile.
                    context.EnvironmentVariables.TryAdd(name, value);
                }

                foreach (var contribution in evaluation.Contributions)
                {
                    contribution.ApplyTo(context.EnvironmentVariables);
                }
            }

            /// <summary>
            /// Applies what the callback at <paramref name="ordinal"/> contributed to the coordinated build.
            /// </summary>
            public async Task ApplyContributionAsync(int ordinal, EnvironmentCallbackContext context)
            {
                var evaluation = await EvaluateOnceAsync(context).ConfigureAwait(false);
                evaluation.Contributions[ordinal].ApplyTo(context.EnvironmentVariables);
            }

            private Task<BuildEnvironmentEvaluation> EvaluateOnceAsync(EnvironmentCallbackContext context)
            {
                Task<BuildEnvironmentEvaluation> evaluation;
                lock (_lock)
                {
                    // A faulted or canceled evaluation is deliberately not retained. The failure usually belongs to the
                    // consumer that happened to trigger it - a canceled resource start, for example - and the other
                    // consumers must still be able to obtain a build environment. Every attempt starts from a fresh
                    // dictionary, so a retry can never observe a half-applied environment.
                    if (_evaluation is null || _evaluation.IsFaulted || _evaluation.IsCanceled)
                    {
                        _evaluation = EvaluateAsync(context);
                    }

                    evaluation = _evaluation;
                }

                // Observe the shared evaluation through the caller's own token so no consumer is held hostage by the
                // cancellation lifetime of whichever consumer started it.
                return evaluation.WaitAsync(context.CancellationToken);
            }

            private async Task<BuildEnvironmentEvaluation> EvaluateAsync(EnvironmentCallbackContext context)
            {
                var launchProfileEnvironment = new Dictionary<string, object>();
                var environment = new Dictionary<string, object>();
                foreach (var (name, value) in _entry.Registration.LaunchProfileEnvironment)
                {
                    var expandedValue = Environment.ExpandEnvironmentVariables(value);
                    launchProfileEnvironment[name] = expandedValue;
                    environment[name] = expandedValue;
                }

                var contributions = new BuildEnvironmentContribution[Callbacks.Count];
                for (var ordinal = 0; ordinal < Callbacks.Count; ordinal++)
                {
                    // The callback observes the project it was registered on, never the build resource, and an
                    // environment that holds build values only. The logger belongs to whichever consumer triggered the
                    // shared evaluation; in practice that is the coordinated build, because the project cannot start
                    // until the build it waits on has produced its environment.
                    var callbackContext = new EnvironmentCallbackContext(
                        context.ExecutionContext,
                        Resource,
                        environment,
                        context.CancellationToken)
                    {
                        Logger = context.Logger,
                    };
                    var before = new Dictionary<string, object>(environment);
                    await Callbacks[ordinal].Callback(callbackContext).ConfigureAwait(false);
                    contributions[ordinal] = BuildEnvironmentContribution.Create(before, environment);
                }

                // Publishing the names here rather than from the build resource keeps the IDE launch configuration
                // consistent with the values the coordinated build used, whichever consumer evaluated first.
                _entry.Metadata.SetBuildEnvironmentVariableNames(environment.Keys);
                return new BuildEnvironmentEvaluation(launchProfileEnvironment, contributions);
            }
        }

        private sealed record BuildEnvironmentEvaluation(
            Dictionary<string, object> LaunchProfileEnvironment,
            BuildEnvironmentContribution[] Contributions);

        /// <summary>
        /// The environment changes a single build callback made, recorded so the project can replay them without
        /// running the callback again.
        /// </summary>
        /// <remarks>
        /// Only the changes are recorded, never the whole environment: replaying a full snapshot onto the project would
        /// also overwrite variables that runtime-only callbacks own, such as an endpoint-derived ASPNETCORE_URLS that
        /// deliberately supersedes the launch profile value. Projects with a runtime callback before a build callback
        /// are rejected during materialization, so all replayed callbacks form one contiguous build-only sequence.
        /// Within that sequence, a callback that writes a value the build environment already held contributes nothing:
        /// the launch profile or an earlier replay already supplied the same value without an intervening callback.
        /// </remarks>
        private sealed record BuildEnvironmentContribution(
            KeyValuePair<string, object>[] AssignedVariables,
            string[] RemovedVariables)
        {
            public static BuildEnvironmentContribution Create(
                Dictionary<string, object> before,
                Dictionary<string, object> after) =>
                new(
                    after
                        .Where(pair =>
                            !before.TryGetValue(pair.Key, out var previousValue) ||
                            !Equals(previousValue, pair.Value))
                        .ToArray(),
                    before.Keys
                        .Where(name => !after.ContainsKey(name))
                        .ToArray());

            public void ApplyTo(Dictionary<string, object> environmentVariables)
            {
                foreach (var name in RemovedVariables)
                {
                    environmentVariables.Remove(name);
                }

                foreach (var (name, value) in AssignedVariables)
                {
                    environmentVariables[name] = value;
                }
            }
        }

        private sealed record ProjectEntry(
            ResourceRegistration Registration,
            DotnetProjectMetadata Metadata);

        private sealed record BuildCallbackSnapshot(
            ProjectEntry Entry,
            EnvironmentCallbackAnnotation[] Callbacks);

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
