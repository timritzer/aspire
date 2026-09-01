// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Roles;

namespace Aspire.Hosting.Azure.Utils;

/// <summary>
/// Builds the federated identity credential that lets a Kubernetes service account exchange its
/// projected token for a Microsoft Entra token issued to a user assigned managed identity.
/// </summary>
/// <remarks>
/// Link-compiled into both <c>Aspire.Hosting.Azure</c> (which owns the public
/// <c>WithKubernetesServiceAccountFederation</c> API) and <c>Aspire.Hosting.Azure.Kubernetes</c>
/// (whose AKS environment federates its own compute resources). The two paths differ in everything
/// that is legitimately path-specific — the Bicep identifier, the resource name, which module the
/// credential lands in, and where the issuer comes from — but <see cref="TokenExchangeAudience"/> and
/// the subject format are matched byte-for-byte by Entra at token-exchange time, so they are defined
/// here once rather than written down twice.
/// </remarks>
internal static class KubernetesFederatedIdentityCredentialFactory
{
    /// <summary>
    /// The only audience Microsoft Entra Workload ID accepts when exchanging a Kubernetes service
    /// account token.
    /// </summary>
    /// <remarks>
    /// See <see href="https://learn.microsoft.com/azure/aks/workload-identity-overview"/>.
    /// </remarks>
    public const string TokenExchangeAudience = "api://AzureADTokenExchange";

    /// <summary>
    /// Builds the <c>sub</c> claim value that Entra matches against the projected service account token.
    /// </summary>
    /// <remarks>
    /// A projected Kubernetes service account token always carries
    /// <c>system:serviceaccount:&lt;namespace&gt;:&lt;name&gt;</c>. Entra compares this exactly, so the
    /// namespace and service account name are used verbatim and are never sanitized — unlike the Azure
    /// resource name, which is arbitrary and therefore free to be normalized.
    /// </remarks>
    public static string CreateServiceAccountSubject(string kubernetesNamespace, string serviceAccountName)
        => $"system:serviceaccount:{kubernetesNamespace}:{serviceAccountName}";

    /// <summary>
    /// Creates a <see cref="FederatedIdentityCredential"/> federating the given Kubernetes service
    /// account to <paramref name="parent"/>.
    /// </summary>
    /// <param name="bicepIdentifier">The Bicep symbolic name for the emitted resource.</param>
    /// <param name="name">The Azure resource name for the credential.</param>
    /// <param name="parent">The user assigned identity the service account may obtain tokens as.</param>
    /// <param name="issuerUri">The cluster's OIDC issuer, as a literal or an unresolved Bicep expression.</param>
    /// <param name="kubernetesNamespace">The namespace containing the service account.</param>
    /// <param name="serviceAccountName">The name of the service account.</param>
    public static FederatedIdentityCredential Create(
        string bicepIdentifier,
        string name,
        UserAssignedIdentity parent,
        BicepValue<Uri> issuerUri,
        string kubernetesNamespace,
        string serviceAccountName)
        => new(bicepIdentifier)
        {
            Parent = parent,
            Name = name,
            IssuerUri = issuerUri,
            Subject = CreateServiceAccountSubject(kubernetesNamespace, serviceAccountName),
            Audiences = { TokenExchangeAudience }
        };
}
