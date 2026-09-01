// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureUserAssignedIdentityFederationTests
{
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_EmitsFederatedCredential()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation(
                   "https://oidc.prod-aks.azure.com/11111111-2222-3333-4444-555555555555/",
                   "my-namespace",
                   "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// The load-bearing case: a compute environment that provisions no cluster supplies the issuer as an
    /// unresolved Bicep expression, which must survive to the template rather than being flattened to a
    /// literal.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_FlowsIssuerExpressionThrough()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var issuerParameter = new ProvisioningParameter("oidcIssuerUrl", typeof(string));

        builder.AddAzureUserAssignedIdentity("myidentity")
               .ConfigureInfrastructure(infrastructure => infrastructure.Add(issuerParameter))
               .WithKubernetesServiceAccountFederation(issuerParameter, "my-namespace", "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    [Fact]
    public async Task WithKubernetesServiceAccountFederation_SupportsMultipleServiceAccounts()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "team-a", "worker")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "team-b", "worker");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    [Fact]
    public async Task WithKubernetesServiceAccountFederation_WorksOnExistingIdentity()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .PublishAsExisting("existing-identity", "existing-rg")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "my-namespace", "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// A service account name is a DNS subdomain, so dots are legal — but ARM rejects them in a federated
    /// identity credential name. The subject must keep the dot; only the resource name is normalized.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_NormalizesDottedServiceAccountName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "my-namespace", "my.workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// A 63-character namespace plus a 253-character service account name overruns the 120-character ARM
    /// limit, so the name is truncated with a digest of the subject appended to keep it distinct.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_TruncatesOverlongNames()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var longNamespace = new string('n', 63);
        var longServiceAccount = new string('s', 200);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", longNamespace, longServiceAccount);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// Re-federating the same service account is a no-op rather than emitting a duplicate resource, which
    /// Bicep would reject with BCP028/BCP121.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_IsIdempotentForTheSameServiceAccount()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "my-namespace", "my-workload")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "my-namespace", "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        await Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// '-' is legal inside both a namespace and a service account name, so ("team-a", "worker") and
    /// ("team", "a-worker") render the same credential name while carrying different subjects. Emitting
    /// both would let one silently replace the other, so this must throw.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_ThrowsOnCollidingServiceAccounts()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "team-a", "worker")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "team", "a-worker");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetManifestWithBicep(resource));

        Assert.Contains("system:serviceaccount:team:a-worker", exception.Message);
        Assert.Contains("system:serviceaccount:team-a:worker", exception.Message);
    }

    [Theory]
    [InlineData("", "sa")]
    [InlineData("ns", "")]
    public void WithKubernetesServiceAccountFederation_ThrowsOnEmptyArguments(string kubernetesNamespace, string serviceAccountName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        Assert.Throws<ArgumentException>(() =>
            identity.WithKubernetesServiceAccountFederation("https://oidc.example.com/", kubernetesNamespace, serviceAccountName));
    }

    /// <summary>
    /// BicepValue&lt;string&gt; converts implicitly from string, so a null or empty issuer arrives as a
    /// non-null BicepValue and would otherwise emit <c>issuer: ''</c> — a template that compiles cleanly
    /// and can never complete token exchange.
    /// </summary>
    [Fact]
    public void WithKubernetesServiceAccountFederation_ThrowsOnEmptyIssuer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        // An empty or null string survives the implicit conversion as a non-null BicepValue carrying
        // nothing, so it is rejected by the value check rather than by ThrowIfNull.
        Assert.Throws<ArgumentException>(() =>
            identity.WithKubernetesServiceAccountFederation("", "my-namespace", "my-workload"));

        Assert.Throws<ArgumentException>(() =>
            identity.WithKubernetesServiceAccountFederation((string)null!, "my-namespace", "my-workload"));

        // A null BicepValue reference is a genuine null argument.
        Assert.Throws<ArgumentNullException>(() =>
            identity.WithKubernetesServiceAccountFederation(default(BicepValue<string>)!, "my-namespace", "my-workload"));
    }

    [Theory]
    [InlineData("My-Namespace", "worker")]
    [InlineData("-namespace", "worker")]
    [InlineData("my.namespace", "worker")]
    [InlineData("my-namespace", "Worker")]
    [InlineData("my-namespace", "worker-")]
    public void WithKubernetesServiceAccountFederation_ThrowsOnInvalidKubernetesNames(string kubernetesNamespace, string serviceAccountName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        Assert.Throws<ArgumentException>(() =>
            identity.WithKubernetesServiceAccountFederation("https://oidc.example.com/", kubernetesNamespace, serviceAccountName));
    }
}
