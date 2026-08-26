// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ProjectRebuilderResourceTests
{
    [Fact]
    public async Task AddProjectRebuilderUsesReleaseAppHostConfiguration()
    {
        var releaseAssembly = typeof(object).Assembly;
        Assert.Equal("Release", releaseAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);

        using var builder = CreateBuilder(releaseAssembly);
        var project = builder.AddProject<Projects.ServiceA>("servicea", options => options.ExcludeLaunchProfile = true);

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder);

        Assert.Equal(
            [
                "build",
                project.Resource.GetProjectMetadata().ProjectPath,
                "--configuration",
                "Release"
            ],
            args);
    }

    [Fact]
    public async Task AddProjectRebuilderOmitsConfigurationWhenAppHostConfigurationIsUnavailable()
    {
        var unconfiguredAssembly = typeof(Enumerable).Assembly;
        Assert.Null(unconfiguredAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>());

        using var builder = CreateBuilder(unconfiguredAssembly);
        var project = builder.AddProject<Projects.ServiceA>("servicea", options => options.ExcludeLaunchProfile = true);

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder);

        Assert.Equal(
            [
                "build",
                project.Resource.GetProjectMetadata().ProjectPath
            ],
            args);
    }

    private static IDistributedApplicationTestingBuilder CreateBuilder(Assembly appHostAssembly)
    {
        return TestDistributedApplicationBuilder.Create(options => options.AssemblyName = appHostAssembly.FullName);
    }
}
