// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
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
    /// application parameter, which must reach the template as an unresolved Bicep parameter AND be bound
    /// in the manifest so a value is actually supplied at deployment time.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_FlowsParameterThrough()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var issuer = builder.AddParameter("oidc-issuer-url");

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation(issuer, "my-namespace", "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        // The manifest binding is the half that a bicep-only assertion cannot see, and the half that was
        // missing when this took a raw BicepValue: the module declared a required parameter that nothing
        // ever supplied.
        await Verify(manifest.ToString(), extension: "json")
            .AppendContentAsFile(bicep, "bicep");
    }

    /// <summary>
    /// An issuer taken from another resource's output — for example an existing AKS cluster's
    /// <c>OidcIssuerUrl</c> — flows through the same parameter binding.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederation_FlowsOutputReferenceThrough()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var cluster = builder.AddAzureInfrastructure("cluster", _ => { });

        builder.AddAzureUserAssignedIdentity("myidentity")
               .WithKubernetesServiceAccountFederation(cluster.GetOutput("oidcIssuerUrl"), "my-namespace", "my-workload");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = Assert.Single(model.Resources.OfType<AzureUserAssignedIdentityResource>());

        var (manifest, bicep) = await GetManifestWithBicep(resource);

        await Verify(manifest.ToString(), extension: "json")
            .AppendContentAsFile(bicep, "bicep");
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
    /// An issuer that carries no value would emit <c>issuer: ''</c> — a template that compiles cleanly
    /// and can never complete token exchange — so every overload rejects it up front.
    /// </summary>
    [Fact]
    public void WithKubernetesServiceAccountFederation_ThrowsOnEmptyIssuer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var identity = builder.AddAzureUserAssignedIdentity("myidentity");

        Assert.Throws<ArgumentException>(() =>
            identity.WithKubernetesServiceAccountFederation("", "my-namespace", "my-workload"));

        // ArgumentException.ThrowIfNullOrEmpty distinguishes the two: null throws ArgumentNullException.
        Assert.Throws<ArgumentNullException>(() =>
            identity.WithKubernetesServiceAccountFederation((string)null!, "my-namespace", "my-workload"));

        Assert.Throws<ArgumentNullException>(() =>
            identity.WithKubernetesServiceAccountFederation((IResourceBuilder<ParameterResource>)null!, "my-namespace", "my-workload"));

        Assert.Throws<ArgumentNullException>(() =>
            identity.WithKubernetesServiceAccountFederation((IManifestExpressionProvider)null!, "my-namespace", "my-workload"));
    }

    /// <summary>
    /// The polyglot dispatcher is the only ATS-exported entry point; the typed overloads opt out. It must
    /// accept both union members and reject anything else.
    /// </summary>
    [Fact]
    public async Task WithKubernetesServiceAccountFederationForPolyglot_DispatchesBothUnionMembers()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var issuer = builder.AddParameter("oidc-issuer-url");

        builder.AddAzureUserAssignedIdentity("literal")
               .WithKubernetesServiceAccountFederationForPolyglot("https://oidc.example.com/cluster/", "my-namespace", "my-workload");

        var parameterIdentity = builder.AddAzureUserAssignedIdentity("parameterized")
               .WithKubernetesServiceAccountFederationForPolyglot(issuer, "my-namespace", "my-workload");

        Assert.Throws<ArgumentException>(() =>
            parameterIdentity.WithKubernetesServiceAccountFederationForPolyglot(42, "my-namespace", "other-workload"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var literal = model.Resources.OfType<AzureUserAssignedIdentityResource>().Single(r => r.Name == "literal");

        var (_, bicep) = await GetManifestWithBicep(literal);

        Assert.Contains("issuer: 'https://oidc.example.com/cluster/'", bicep);
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
