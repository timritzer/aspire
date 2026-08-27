// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Configures the shared initial build for path-based .NET resources.
/// </summary>
internal static class DotnetProjectBuildCoordinator
{
    // DCP uses -1 while an executable's process exit code is not yet available. The constant is
    // internal to Aspire.Hosting, so keep the protocol value here until it is normalized at the boundary.
    private const int UnknownExitCode = -1;
    private const string UndefinedSolutionPropertyValue = "*Undefined*";

    private static readonly string[] s_solutionIdentityPropertyNames =
    [
        "SolutionDir",
        "SolutionExt",
        "SolutionFileName",
        "SolutionName",
        "SolutionPath"
    ];

    internal const string BuildResourceName = "__dotnet-project-build";

    public static DotnetProjectBuildResource? Prepare(
        IDistributedApplicationBuilder builder,
        DotnetProjectMetadata projectMetadata)
    {
        if (!builder.ExecutionContext.IsRunMode)
        {
            return null;
        }

        var projectPath = projectMetadata.ProjectPath;
        var buildResource = builder.Resources
            .OfType<DotnetProjectBuildResource>()
            .SingleOrDefault();

        if (IsProjectFile(projectPath))
        {
            projectMetadata.SuppressBuild = true;
            buildResource ??= AddBuildResource(builder, projectMetadata.BuildConfiguration);
            projectMetadata.SetProjectPath(buildResource.AddProject(projectPath));
        }

        return buildResource;
    }

    public static void Configure(
        IResourceBuilder<DotnetProjectResource> resourceBuilder,
        DotnetProjectBuildResource? buildResource)
    {
        if (buildResource is null)
        {
            return;
        }

        // File-based apps cannot participate in the synthetic solution, but they can reference projects
        // whose outputs the solution is building. Gate every path-based .NET resource on the same build so
        // those individual file compilations cannot race the coordinated project graph.
        foreach (var resource in resourceBuilder.ApplicationBuilder.Resources.OfType<DotnetProjectResource>())
        {
            if (resource.Annotations.OfType<DotnetProjectMetadata>().SingleOrDefault() is { } metadata &&
                IsSupportedPath(metadata.ProjectPath))
            {
                AddBuildDependency(resourceBuilder.ApplicationBuilder, resource, buildResource);
            }
        }
    }

    private static DotnetProjectBuildResource AddBuildResource(
        IDistributedApplicationBuilder builder,
        string? configuration)
    {
        var buildResource = new DotnetProjectBuildResource(BuildResourceName, builder.AppHostDirectory, TimeProvider.System);
        buildResource.Annotations.Add(NameValidationPolicyAnnotation.None);
        // A factory registration makes the service provider own and dispose this existing model resource.
        // The instance registration overload would leave disposal with the caller.
        builder.Services.AddSingleton(_ => buildResource);
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var ownedBuildResource = @event.Services.GetRequiredService<DotnetProjectBuildResource>();
            ownedBuildResource.RegisterForShutdown(@event.Services.GetRequiredService<IHostApplicationLifetime>());
            return Task.CompletedTask;
        });

        builder.AddResource(buildResource)
            .WithArgs(async context =>
            {
                var solutionPath = await buildResource.WriteSolutionAsync(context.Logger, context.CancellationToken).ConfigureAwait(false);

                context.Args.Add("build");
                context.Args.Add(solutionPath);

                // Building a solution normally injects its path and name into every project. The generated
                // solution lives under .aspire/build and has a content-derived name, so exposing that identity
                // would change project evaluation from the previous direct-project builds and from later
                // per-project rebuilds. Restore the values MSBuild supplies for a direct project build.
                foreach (var propertyName in s_solutionIdentityPropertyNames)
                {
                    context.Args.Add($"-p:{propertyName}={UndefinedSolutionPropertyValue}");
                }

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

    private static void AddBuildDependency(
        IDistributedApplicationBuilder builder,
        DotnetProjectResource resource,
        DotnetProjectBuildResource buildResource)
    {
        if (resource.Annotations.OfType<WaitAnnotation>().Any(
            annotation => annotation.WaitType is WaitType.WaitForCompletion &&
                          ReferenceEquals(annotation.Resource, buildResource)))
        {
            return;
        }

        builder.CreateResourceBuilder(resource)
            .WaitForCompletion(builder.CreateResourceBuilder(buildResource))
            .OnBeforeResourceStarted((_, @event, cancellationToken) =>
                WaitForSuccessfulBuildAsync(@event.Services, buildResource, cancellationToken));
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
         snapshot.ExitCode is not null and not UnknownExitCode);

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedPath(string path) =>
        IsProjectFile(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
