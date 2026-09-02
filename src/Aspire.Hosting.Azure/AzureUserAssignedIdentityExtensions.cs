// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Utils;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Roles;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Provides extension methods for working with Azure user‑assigned identities.
/// </summary>
public static class AzureUserAssignedIdentityExtensions
{
    /// <summary>
    /// Adds an Azure user‑assigned identity resource to the application model.
    /// </summary>
    /// <param name="builder">The builder for the distributed application.</param>
    /// <param name="name">The name of the resource.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is null or empty.</exception>
    /// <remarks>
    /// This method adds an Azure user‑assigned identity resource to the application model. It configures the
    /// infrastructure for the resource and returns a builder for the resource.
    /// The resource is added to the infrastructure only if the application is not in run mode.
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureUserAssignedIdentityResource}"/> builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<AzureUserAssignedIdentityResource> AddAzureUserAssignedIdentity(
        this IDistributedApplicationBuilder builder,
        string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddAzureProvisioning();

        var resource = new AzureUserAssignedIdentityResource(name);
        // Don't add the resource to the infrastructure if we're in run mode.
        if (builder.ExecutionContext.IsRunMode)
        {
            return builder.CreateResourceBuilder(resource);
        }

        return builder.AddResource(resource);
    }

    /// <summary>
    /// Attaches an existing <see cref="AzureUserAssignedIdentityResource"/> to a compute resource, 
    /// setting it as the target identity for the builder.
    /// </summary>
    /// <ats-summary>Associates an Azure user-assigned identity with a compute resource</ats-summary>
    /// <param name="builder">The builder for the <see cref="IComputeResource"/> the identity will be associated with.</param>
    /// <param name="identityResourceBuilder">The builder for the <see cref="AzureUserAssignedIdentityResource"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{IComputeResource}"/> builder.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <example>
    /// <code>
    /// var identity = builder.AddAzureUserAssignedIdentity("myIdentity");
    /// var app = builder.AddProject("myApp")
    ///     .WithAzureUserAssignedIdentity(identity);
    /// </code>
    /// </example>
    [AspireExport("withUserAssignedIdentityAzureUserAssignedIdentity", MethodName = "withAzureUserAssignedIdentity")]
    public static IResourceBuilder<T> WithAzureUserAssignedIdentity<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureUserAssignedIdentityResource> identityResourceBuilder)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(identityResourceBuilder);

        builder.WithAnnotation(new AppIdentityAnnotation(identityResourceBuilder.Resource));

        return builder;
    }

    /// <summary>
    /// Emits a federated identity credential allowing the Kubernetes service account
    /// <c>system:serviceaccount:{kubernetesNamespace}:{serviceAccountName}</c> to obtain Microsoft Entra
    /// tokens as <paramref name="identity"/>.
    /// </summary>
    /// <param name="identity">The builder for the <see cref="AzureUserAssignedIdentityResource"/> to federate.</param>
    /// <param name="oidcIssuerUrl">The OIDC issuer URL of the cluster that will present the service account token.</param>
    /// <param name="kubernetesNamespace">The namespace containing the Kubernetes service account.</param>
    /// <param name="serviceAccountName">The name of the Kubernetes service account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureUserAssignedIdentityResource}"/> builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="identity"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="oidcIssuerUrl"/> is null or empty, or when
    /// <paramref name="kubernetesNamespace"/> or <paramref name="serviceAccountName"/> is not a valid
    /// Kubernetes name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the identity cannot be resolved, or when a different service account already occupies
    /// the same generated credential name.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The OIDC issuer is supplied as a value rather than read from a provisioned cluster, so this works
    /// against a pre-existing cluster and for compute environments that provision no cluster of their own.
    /// <c>AddAzureKubernetesEnvironment</c> already federates its own compute resources; this method covers
    /// the cases that environment does not.
    /// </para>
    /// <para>
    /// This emits only the Azure half of workload identity. The cluster half — a <c>ServiceAccount</c>
    /// annotated with the identity's client ID and a pod labelled <c>azure.workload.identity/use</c> — is
    /// the caller's responsibility, and the service account it creates must match
    /// <paramref name="kubernetesNamespace"/> and <paramref name="serviceAccountName"/> exactly or token
    /// exchange will fail.
    /// </para>
    /// <para>
    /// Calling this twice for the same service account is a no-op. Calling it for a different service
    /// account that happens to produce the same credential name throws rather than silently replacing the
    /// first credential.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
    ///
    /// identity.WithKubernetesServiceAccountFederation(
    ///     oidcIssuerUrl: "https://oidc.example.com/cluster/",
    ///     kubernetesNamespace: "my-namespace",
    ///     serviceAccountName: "my-workload");
    ///
    /// // The cluster half, via the public KubernetesServiceCustomizationAnnotation:
    /// //   serviceAccount.Metadata.Name = "my-workload";
    /// //   serviceAccount.Metadata.Annotations["azure.workload.identity/client-id"] = clientId;
    /// //   podSpec.ServiceAccountName = "my-workload";
    /// //   podTemplate.Metadata.Labels["azure.workload.identity/use"] = "true";
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Use the polyglot withKubernetesServiceAccountFederation overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<AzureUserAssignedIdentityResource> WithKubernetesServiceAccountFederation(
        this IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        string oidcIssuerUrl,
        string kubernetesNamespace,
        string serviceAccountName)
    {
        ArgumentException.ThrowIfNullOrEmpty(oidcIssuerUrl);

        // A literal needs no Bicep parameter, so it is written straight into the template.
        return WithKubernetesServiceAccountFederationCore(
            identity,
            _ => new StringLiteralExpression(oidcIssuerUrl),
            kubernetesNamespace,
            serviceAccountName);
    }

    /// <summary>
    /// Emits a federated identity credential allowing the Kubernetes service account
    /// <c>system:serviceaccount:{kubernetesNamespace}:{serviceAccountName}</c> to obtain Microsoft Entra
    /// tokens as <paramref name="identity"/>, taking the issuer from an application parameter.
    /// </summary>
    /// <param name="identity">The builder for the <see cref="AzureUserAssignedIdentityResource"/> to federate.</param>
    /// <param name="oidcIssuerUrl">A parameter supplying the OIDC issuer URL of the cluster.</param>
    /// <param name="kubernetesNamespace">The namespace containing the Kubernetes service account.</param>
    /// <param name="serviceAccountName">The name of the Kubernetes service account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureUserAssignedIdentityResource}"/> builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="identity"/> or <paramref name="oidcIssuerUrl"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="kubernetesNamespace"/> or <paramref name="serviceAccountName"/> is not
    /// a valid Kubernetes name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the identity cannot be resolved, or when a different service account already occupies
    /// the same generated credential name.
    /// </exception>
    /// <remarks>
    /// The parameter is emitted as a Bicep parameter on the identity's module and bound to the
    /// application parameter, so its value is supplied at deployment time.
    /// </remarks>
    /// <example>
    /// <code>
    /// var issuer = builder.AddParameter("oidc-issuer-url");
    /// var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
    ///
    /// identity.WithKubernetesServiceAccountFederation(issuer, "my-namespace", "my-workload");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Use the polyglot withKubernetesServiceAccountFederation overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<AzureUserAssignedIdentityResource> WithKubernetesServiceAccountFederation(
        this IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        IResourceBuilder<ParameterResource> oidcIssuerUrl,
        string kubernetesNamespace,
        string serviceAccountName)
    {
        ArgumentNullException.ThrowIfNull(oidcIssuerUrl);

        return WithKubernetesServiceAccountFederationCore(
            identity,
            infrastructure => oidcIssuerUrl.AsProvisioningParameter(infrastructure),
            kubernetesNamespace,
            serviceAccountName);
    }

    /// <summary>
    /// Emits a federated identity credential allowing the Kubernetes service account
    /// <c>system:serviceaccount:{kubernetesNamespace}:{serviceAccountName}</c> to obtain Microsoft Entra
    /// tokens as <paramref name="identity"/>, taking the issuer from another resource's value.
    /// </summary>
    /// <param name="identity">The builder for the <see cref="AzureUserAssignedIdentityResource"/> to federate.</param>
    /// <param name="oidcIssuerUrl">
    /// A value supplying the OIDC issuer URL, such as the <c>OidcIssuerUrl</c> output of an existing
    /// Azure Kubernetes Service cluster.
    /// </param>
    /// <param name="kubernetesNamespace">The namespace containing the Kubernetes service account.</param>
    /// <param name="serviceAccountName">The name of the Kubernetes service account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureUserAssignedIdentityResource}"/> builder.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="identity"/> or <paramref name="oidcIssuerUrl"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="kubernetesNamespace"/> or <paramref name="serviceAccountName"/> is not
    /// a valid Kubernetes name.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the identity cannot be resolved, or when a different service account already occupies
    /// the same generated credential name.
    /// </exception>
    /// <remarks>
    /// The value is emitted as a Bicep parameter on the identity's module and bound to the supplied
    /// expression, so it is resolved at deployment time.
    /// </remarks>
    /// <example>
    /// <code>
    /// var cluster = builder.AddAzureKubernetesEnvironment("aks");
    /// var identity = builder.AddAzureUserAssignedIdentity("workload-identity");
    ///
    /// identity.WithKubernetesServiceAccountFederation(
    ///     cluster.Resource.OidcIssuerUrl, "my-namespace", "my-workload");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "IManifestExpressionProvider parameters are not ATS-compatible. Use the polyglot withKubernetesServiceAccountFederation overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<AzureUserAssignedIdentityResource> WithKubernetesServiceAccountFederation(
        this IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        IManifestExpressionProvider oidcIssuerUrl,
        string kubernetesNamespace,
        string serviceAccountName)
    {
        ArgumentNullException.ThrowIfNull(oidcIssuerUrl);

        return WithKubernetesServiceAccountFederationCore(
            identity,
            infrastructure => oidcIssuerUrl.AsProvisioningParameter(infrastructure),
            kubernetesNamespace,
            serviceAccountName);
    }

    /// <summary>
    /// Emits a federated identity credential linking a Kubernetes service account to a user assigned identity.
    /// </summary>
    /// <param name="identity">The builder for the <see cref="AzureUserAssignedIdentityResource"/> to federate.</param>
    /// <param name="oidcIssuerUrl">The OIDC issuer URL as a string or parameter resource.</param>
    /// <param name="kubernetesNamespace">The namespace containing the Kubernetes service account.</param>
    /// <param name="serviceAccountName">The name of the Kubernetes service account.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{AzureUserAssignedIdentityResource}"/> builder.</returns>
    [AspireExport("withKubernetesServiceAccountFederation")]
    internal static IResourceBuilder<AzureUserAssignedIdentityResource> WithKubernetesServiceAccountFederationForPolyglot(
        this IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object oidcIssuerUrl,
        string kubernetesNamespace,
        string serviceAccountName)
    {
        ArgumentNullException.ThrowIfNull(oidcIssuerUrl);

        return oidcIssuerUrl switch
        {
            string issuerValue => identity.WithKubernetesServiceAccountFederation(issuerValue, kubernetesNamespace, serviceAccountName),
            IResourceBuilder<ParameterResource> issuerParameter => identity.WithKubernetesServiceAccountFederation(issuerParameter, kubernetesNamespace, serviceAccountName),
            _ => throw new ArgumentException(
                "The OIDC issuer URL must be a string or a parameter resource builder.",
                nameof(oidcIssuerUrl))
        };
    }

    /// <summary>
    /// Shared implementation for the <c>WithKubernetesServiceAccountFederation</c> overloads.
    /// </summary>
    /// <param name="identity">The identity to federate.</param>
    /// <param name="resolveIssuer">
    /// Produces the issuer value once the module's infrastructure exists. Deferred because the
    /// parameter-backed overloads must register a Bicep parameter on that infrastructure, which also binds
    /// the value into the module's parameter map so it is supplied at deployment time.
    /// </param>
    /// <param name="kubernetesNamespace">The namespace containing the Kubernetes service account.</param>
    /// <param name="serviceAccountName">The name of the Kubernetes service account.</param>
    private static IResourceBuilder<AzureUserAssignedIdentityResource> WithKubernetesServiceAccountFederationCore(
        IResourceBuilder<AzureUserAssignedIdentityResource> identity,
        Func<AzureResourceInfrastructure, BicepValue<Uri>> resolveIssuer,
        string kubernetesNamespace,
        string serviceAccountName)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(kubernetesNamespace);
        ArgumentException.ThrowIfNullOrEmpty(serviceAccountName);

        // RFC 1123: a namespace is a DNS label, a service account name is a DNS subdomain (dots allowed).
        // https://kubernetes.io/docs/concepts/overview/working-with-objects/names/
        ValidateKubernetesName(kubernetesNamespace, allowDots: false, maxLength: 63, nameof(kubernetesNamespace));
        ValidateKubernetesName(serviceAccountName, allowDots: true, maxLength: 253, nameof(serviceAccountName));

        return identity.ConfigureInfrastructure(infrastructure =>
        {
            // The credential is emitted into the identity's own module, so it parents directly to the
            // UserAssignedIdentity that AzureUserAssignedIdentityResource has already added. That is why
            // there is no FromExisting plus name-parameter round trip here: the AKS environment needs one
            // only because it emits the credential into a *different* module.
            //
            // Match on the identifier rather than taking the single UserAssignedIdentity present, so a
            // second identity added by another ConfigureInfrastructure callback cannot make this ambiguous.
            var identityIdentifier = identity.Resource.GetBicepIdentifier();
            var userAssignedIdentity = infrastructure.GetProvisionableResources()
                .OfType<UserAssignedIdentity>()
                .SingleOrDefault(u => u.BicepIdentifier == identityIdentifier)
                ?? throw new InvalidOperationException(
                    $"Could not resolve the user assigned identity '{identityIdentifier}' for resource '{identity.Resource.Name}' while adding a federated identity credential.");

            var credentialName = CreateFederatedCredentialName(kubernetesNamespace, serviceAccountName);
            var credentialIdentifier = Infrastructure.NormalizeBicepIdentifier($"fedcred_{credentialName}");
            var subject = KubernetesFederatedIdentityCredentialFactory.CreateServiceAccountSubject(kubernetesNamespace, serviceAccountName);

            var existing = infrastructure.GetProvisionableResources()
                .OfType<FederatedIdentityCredential>()
                .FirstOrDefault(c => c.BicepIdentifier == credentialIdentifier);

            if (existing is not null)
            {
                // Two distinct service accounts can land on one name because '-' is legal inside both a
                // namespace and a service account name, so ("team-a", "worker") and ("team", "a-worker")
                // both render as "team-a-worker-fedcred". Emitting both produces a template Bicep rejects
                // (BCP028/BCP121), and if it were accepted one credential would silently replace the
                // other and federate the wrong subject. Re-federating the *same* subject is harmless.
                if (existing.Subject.Value == subject)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"Cannot federate '{subject}' to identity '{identity.Resource.Name}': the federated credential name '{credentialName}' is already used by '{existing.Subject.Value}'. Rename the Kubernetes namespace or service account so the two do not collide.");
            }

            infrastructure.Add(KubernetesFederatedIdentityCredentialFactory.Create(
                bicepIdentifier: credentialIdentifier,
                name: credentialName,
                parent: userAssignedIdentity,
                // The issuer is never routed through System.Uri: Uri would normalize the value (for
                // example appending a trailing slash to an authority-only URL) and Entra compares the
                // issuer against the token's `iss` claim byte for byte.
                issuerUri: resolveIssuer(infrastructure),
                kubernetesNamespace: kubernetesNamespace,
                serviceAccountName: serviceAccountName));
        });
    }

    /// <summary>
    /// Builds the Azure resource name for a federated identity credential.
    /// </summary>
    /// <remarks>
    /// Unlike the subject, this name is arbitrary metadata that Entra never matches, so it is free to be
    /// normalized. ARM constrains it to <c>^[a-zA-Z0-9]{1}[a-zA-Z0-9-_]{2,119}$</c>, which excludes the
    /// dots that are legal in a service account name.
    /// </remarks>
    private static string CreateFederatedCredentialName(string kubernetesNamespace, string serviceAccountName)
    {
        var candidate = $"{kubernetesNamespace}-{serviceAccountName}-fedcred".Replace('.', '-');

        if (candidate.Length <= MaxFederatedCredentialNameLength)
        {
            return candidate;
        }

        // A 63-character namespace plus a 253-character service account name overruns the ARM limit, and a
        // plain truncation would map distinct service accounts onto one credential. Append a digest of the
        // exact subject so long names stay distinct.
        var subject = KubernetesFederatedIdentityCredentialFactory.CreateServiceAccountSubject(kubernetesNamespace, serviceAccountName);
        var digest = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(subject)).ToString("x16", CultureInfo.InvariantCulture);

        return string.Concat(candidate.AsSpan(0, MaxFederatedCredentialNameLength - digest.Length - 1), "-", digest);
    }

    /// <summary>
    /// Validates a Kubernetes namespace or service account name against RFC 1123.
    /// </summary>
    /// <remarks>
    /// Kubernetes rejects anything outside RFC 1123 at admission time, so a value failing here could never
    /// have matched a real service account token — the failure would otherwise surface as an unexplained
    /// 401 inside the cluster rather than an error at publish time. It also guarantees the derived Azure
    /// resource name starts with an alphanumeric, as ARM requires.
    /// </remarks>
    private static void ValidateKubernetesName(string value, bool allowDots, int maxLength, string paramName)
    {
        var valid = value.Length <= maxLength
            && char.IsAsciiLetterOrDigit(value[0])
            && char.IsAsciiLetterOrDigit(value[^1]);

        if (valid)
        {
            foreach (var c in value)
            {
                if (char.IsAsciiLetterOrDigit(c) && !char.IsAsciiLetterUpper(c))
                {
                    continue;
                }

                if (c == '-' || (allowDots && c == '.'))
                {
                    continue;
                }

                valid = false;
                break;
            }
        }

        if (!valid)
        {
            var allowed = allowDots ? "lowercase alphanumerics, '-' and '.'" : "lowercase alphanumerics and '-'";
            throw new ArgumentException(
                $"'{value}' is not a valid Kubernetes name. It must be at most {maxLength} characters, contain only {allowed}, and start and end with an alphanumeric.",
                paramName);
        }
    }

    /// <summary>
    /// The maximum length ARM accepts for a federated identity credential name.
    /// </summary>
    private const int MaxFederatedCredentialNameLength = 120;
}
