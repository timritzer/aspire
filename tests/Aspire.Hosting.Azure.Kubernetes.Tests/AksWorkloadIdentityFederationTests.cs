// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

/// <summary>
/// Covers the AKS environment's own federated credential emission, which shares
/// <c>KubernetesFederatedIdentityCredentialFactory</c> with the public
/// <c>WithKubernetesServiceAccountFederation</c> API.
/// </summary>
public class AksWorkloadIdentityFederationTests
{
    [Fact]
    public async Task AksEnvironment_EmitsFederatedCredentialForWorkloadIdentity()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        // Populated by the environment's prepare-aks pipeline step for every compute resource carrying an
        // AppIdentityAnnotation; set directly so the Bicep emission can be asserted in isolation.
        aks.Resource.WorkloadIdentities["myapi"] = identity.Resource;

        var (_, bicep) = await GetManifestWithBicep(aks.Resource);

        await Verify(bicep, extension: "bicep");
    }

    /// <summary>
    /// The AKS path derives its subject from an Aspire resource name rather than from caller-supplied
    /// Kubernetes names, and it defaults the namespace to <c>default</c>. Both differ from the public API,
    /// so they are pinned here independently of the shared factory.
    /// </summary>
    [Fact]
    public async Task AksEnvironment_UsesResourceNameAndDefaultNamespaceForSubject()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        aks.Resource.WorkloadIdentities["myapi"] = identity.Resource;

        var (_, bicep) = await GetManifestWithBicep(aks.Resource);

        Assert.Contains("subject: 'system:serviceaccount:default:myapi-sa'", bicep);
        Assert.Contains("'api://AzureADTokenExchange'", bicep);
    }
}
