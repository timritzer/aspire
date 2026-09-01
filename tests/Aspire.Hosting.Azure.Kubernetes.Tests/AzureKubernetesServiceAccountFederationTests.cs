// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesServiceAccountFederationTests
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
    /// The load-bearing case from the API proposal: a compute environment that provisions no cluster
    /// supplies the issuer as an unresolved Bicep expression, so it must survive to the template intact
    /// rather than being flattened to a literal.
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

    /// <summary>
    /// The same service account name in two namespaces is ordinary in Kubernetes, so it must not collide
    /// on either the Bicep identifier or the Azure resource name.
    /// </summary>
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
    public async Task WithKubernetesServiceAccountFederation_BuildsCredentialWithSubjectAndAudience()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation("https://oidc.example.com/cluster/", "my-namespace", "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (_, bicep) = await GetManifestWithBicep(resource);

        // Asserted explicitly rather than left to the snapshot alone: Entra matches both of these byte
        // for byte, and a wrong value fails at token-exchange time rather than at deploy time. Pinning
        // them here means a regression cannot be waved through by re-accepting a snapshot.
        Assert.Contains("subject: 'system:serviceaccount:my-namespace:my-workload'", bicep);
        Assert.Contains("'api://AzureADTokenExchange'", bicep);
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
    /// Covers the AKS environment's own federated credential emission, which shares the credential-building
    /// helper with <c>WithKubernetesServiceAccountFederation</c>. Guards the subject and audience the AKS
    /// path produces, which had no snapshot coverage previously.
    /// </summary>
    [Fact]
    public async Task AksEnvironment_EmitsFederatedCredentialForWorkloadIdentity()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        // Populated by the environment's prepare-aks pipeline step for every compute resource carrying
        // an AppIdentityAnnotation; set directly so the Bicep emission can be asserted in isolation.
        aks.Resource.WorkloadIdentities["myapi"] = identity.Resource;

        var (_, bicep) = await GetManifestWithBicep(aks.Resource);

        await Verify(bicep, extension: "bicep");
    }
}
