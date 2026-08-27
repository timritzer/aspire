// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001, ASPIREPIPELINES001

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

        var launchProfile = resourceBuilder.Resource.GetEffectiveLaunchProfile()?.LaunchProfile;
        state.AddResource(
            resourceBuilder.Resource,
            hasLaunchProfileEnvironment: launchProfile?.EnvironmentVariables.Count > 0);

        // Preserve the eagerly visible dependency used by model tests and tooling. BeforeStart replaces
        // the build plan after all resource environment callbacks and SDK roots are known, then adds the
        // final build barrier as an additional dependency.
        if (state.PrimaryBuildResource is { } primaryBuildResource)
        {
            foreach (var resource in state.Resources)
            {
                if (resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is { } metadata &&
                    IsSupportedPath(metadata.ProjectPath))
                {
                    AddBuildDependency(resourceBuilder.ApplicationBuilder, resource, primaryBuildResource);
                }
            }
        }
    }

    private static DotnetProjectBuildResource AddBuildResource(
        IDistributedApplicationBuilder builder,
        string name,
        string? configuration,
        bool registerWithServices)
    {
        var buildDirectory = Path.Combine(builder.AppHostDirectory, ".aspire", "build");
        var buildResource = new DotnetProjectBuildResource(
            name,
            builder.AppHostDirectory,
            buildDirectory,
            TimeProvider.System);
        buildResource.Annotations.Add(NameValidationPolicyAnnotation.None);

        if (registerWithServices)
        {
            // A factory registration makes the service provider own and dispose this existing model resource.
            // The instance registration overload would leave disposal with the caller.
            builder.Services.AddSingleton(_ => buildResource);
        }

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
        private bool _materialized;
        private bool _disposed;

        public CoordinatorState(IDistributedApplicationBuilder builder)
        {
            _builder = builder;
            builder.Services.AddSingleton(_ => this);
            builder.Pipeline.AddPipelineConfiguration(context =>
            {
                var beforeStartStep = context.Steps.Single(step => step.Name == WellKnownPipelineSteps.BeforeStart);
                beforeStartStep.SetFinalAction(stepContext => stepContext.Services
                    .GetRequiredService<CoordinatorState>()
                    .MaterializeBuildPlan(stepContext.Services));
                return Task.CompletedTask;
            });
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

        public void AddResource(DotnetProjectResource resource, bool hasLaunchProfileEnvironment)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var baselineEnvironmentCallbacks = resource.TryGetEnvironmentVariables(out var environmentCallbacks)
                ? new HashSet<EnvironmentCallbackAnnotation>(environmentCallbacks, ReferenceEqualityComparer.Instance)
                : [];
            _registrations.Add(new ResourceRegistration(
                resource,
                baselineEnvironmentCallbacks,
                hasLaunchProfileEnvironment));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var buildResource in _ownedBuildResources)
            {
                buildResource.Dispose();
            }
        }

        internal Task MaterializeBuildPlan(IServiceProvider services)
        {
            if (_materialized)
            {
                return Task.CompletedTask;
            }

            var projectEntries = _registrations
                .Select(registration => new ProjectEntry(
                    registration,
                    registration.Resource.Annotations.OfType<DotnetProjectMetadata>().Single()))
                .Where(entry => IsProjectFile(entry.Metadata.ProjectPath))
                .ToArray();
            if (projectEntries.Length == 0)
            {
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
                        var environmentAnnotation = CopyProjectEnvironment(entry.Registration.Resource, buildResource);
                        rollbackActions.Push(() => buildResource.Annotations.Remove(environmentAnnotation));
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
                foreach (var registration in _registrations)
                {
                    var resource = registration.Resource;
                    if (resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is { } metadata &&
                        IsSupportedPath(metadata.ProjectPath) &&
                        AddBuildDependency(_builder, resource, finalBuildResource) is { } rollback)
                    {
                        rollbackActions.Push(rollback);
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
                configuration,
                registerWithServices: ordinal is null);
            _ownedBuildResources.Add(buildResource);
            return buildResource;
        }

        private static EnvironmentCallbackAnnotation CopyProjectEnvironment(
            DotnetProjectResource projectResource,
            DotnetProjectBuildResource buildResource)
        {
            var annotation = new EnvironmentCallbackAnnotation(async context =>
            {
                var projectConfiguration = await ExecutionConfigurationBuilder
                    .Create(projectResource)
                    .WithEnvironmentVariablesConfig()
                    .BuildAsync(
                        context.ExecutionContext,
                        context.Logger,
                        context.CancellationToken)
                    .ConfigureAwait(false);

                if (projectConfiguration.Exception is not null)
                {
                    ExceptionDispatchInfo.Throw(projectConfiguration.Exception);
                }

                foreach (var (name, value) in projectConfiguration.EnvironmentVariablesWithUnprocessed)
                {
                    context.EnvironmentVariables[name] = value.Unprocessed;
                }
            });
            buildResource.Annotations.Add(annotation);
            return annotation;
        }

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
            if (entry.Registration.HasLaunchProfileEnvironment)
            {
                return true;
            }

            if (!entry.Registration.Resource.TryGetEnvironmentVariables(out var environmentCallbacks))
            {
                return false;
            }

            return environmentCallbacks.Any(
                callback => callback is not RuntimeEnvironmentCallbackAnnotation &&
                            !entry.Registration.BaselineEnvironmentCallbacks.Contains(callback));
        }

        private readonly record struct BuildContext(string? GlobalJsonPath, string? Configuration);

        private sealed record ProjectEntry(
            ResourceRegistration Registration,
            DotnetProjectMetadata Metadata);

        private sealed record ResourceRegistration(
            DotnetProjectResource Resource,
            HashSet<EnvironmentCallbackAnnotation> BaselineEnvironmentCallbacks,
            bool HasLaunchProfileEnvironment);

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
