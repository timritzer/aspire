// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Annotation that marks a <see cref="KubernetesGatewayResource"/> as referring to a Gateway that
/// already exists in the cluster. When present, no <c>Gateway</c> object is generated and the
/// <c>HTTPRoute</c> resources produced for the gateway carry a <c>parentRef</c> targeting the
/// referenced Gateway instead.
/// </summary>
/// <remarks>
/// The referenced Gateway — and therefore its listeners, TLS configuration, and
/// <c>allowedRoutes</c> — is owned by the platform rather than by Aspire, so gateway lifecycle
/// steps (TLS bootstrap, FQDN discovery, cert-manager solver routes, and field-manager cleanup)
/// are skipped for it.
/// </remarks>
/// <param name="name">The <c>metadata.name</c> of the existing Gateway object.</param>
/// <param name="namespace">The namespace of the existing Gateway, or <see langword="null"/> to resolve the reference within the deployment's namespace.</param>
/// <param name="sectionName">The listener (section) name to attach to, or <see langword="null"/> to attach to every compatible listener.</param>
internal sealed class ExistingKubernetesGatewayAnnotation(
    ReferenceExpression name,
    ReferenceExpression? @namespace = null,
    ReferenceExpression? sectionName = null) : IResourceAnnotation
{
    /// <summary>
    /// Gets the <c>metadata.name</c> of the existing Gateway object.
    /// </summary>
    public ReferenceExpression Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>
    /// Gets the namespace of the existing Gateway. When <see langword="null"/> the reference
    /// resolves within the deployment's namespace.
    /// </summary>
    public ReferenceExpression? Namespace { get; } = @namespace;

    /// <summary>
    /// Gets the listener (section) name on the existing Gateway. When <see langword="null"/>
    /// routes attach to every compatible listener.
    /// </summary>
    public ReferenceExpression? SectionName { get; } = sectionName;
}
