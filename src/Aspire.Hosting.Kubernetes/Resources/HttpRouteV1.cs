// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents an HTTPRoute resource in Kubernetes (gateway.networking.k8s.io/v1).
/// HTTPRoute defines HTTP routing rules that attach to a Gateway.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteV1() : BaseKubernetesResource("gateway.networking.k8s.io/v1", "HTTPRoute")
{
    /// <summary>
    /// Gets or sets the specification of the HTTPRoute resource.
    /// </summary>
    [YamlMember(Alias = "spec")]
    public HttpRouteSpecV1 Spec { get; set; } = new();
}

/// <summary>
/// Represents the specification of an HTTPRoute resource.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteSpecV1
{
    /// <summary>
    /// Gets the parent references that this route attaches to (typically Gateway resources).
    /// </summary>
    [YamlMember(Alias = "parentRefs")]
    public List<HttpRouteParentRefV1> ParentRefs { get; } = [];

    /// <summary>
    /// Gets the hostnames that this route matches. If empty, matches all hostnames.
    /// </summary>
    [YamlMember(Alias = "hostnames")]
    public List<string> Hostnames { get; } = [];

    /// <summary>
    /// Gets the routing rules for this HTTPRoute.
    /// </summary>
    [YamlMember(Alias = "rules")]
    public List<HttpRouteRuleV1> Rules { get; } = [];
}

/// <summary>
/// A reference to a parent resource (typically a Gateway) that an HTTPRoute attaches to.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteParentRefV1
{
    /// <summary>
    /// Gets or sets the name of the parent Gateway resource.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the namespace of the parent Gateway resource.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the reference resolves to the same namespace as the HTTPRoute.
    /// Set this to attach the route to a Gateway that lives in a different namespace (for example a
    /// shared, platform-owned Gateway). The target Gateway must permit cross-namespace attachment via
    /// its listeners' <c>allowedRoutes</c>. See
    /// <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#attaching-to-gateways"/>.
    /// </remarks>
    [YamlMember(Alias = "namespace")]
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets the name of a specific listener (section) on the parent Gateway to attach to.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the route attaches to every compatible listener on the Gateway.
    /// Set this to the listener's <c>name</c> to bind the route to a single listener (for example an
    /// HTTPS listener). See
    /// <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#attaching-to-gateways"/>.
    /// </remarks>
    [YamlMember(Alias = "sectionName")]
    public string? SectionName { get; set; }

    /// <summary>
    /// Gets or sets the API group of the parent resource.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the Gateway API core group (<c>gateway.networking.k8s.io</c>) is
    /// assumed. Set this only when attaching to a parent defined by a different API group.
    /// </remarks>
    [YamlMember(Alias = "group")]
    public string? Group { get; set; }

    /// <summary>
    /// Gets or sets the kind of the parent resource.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the parent is assumed to be a <c>Gateway</c>. Set this only when
    /// attaching to a parent of a different kind.
    /// </remarks>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }
}

/// <summary>
/// A single routing rule in an HTTPRoute. Each rule matches requests and forwards
/// them to one or more backend services.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteRuleV1
{
    /// <summary>
    /// Gets the match conditions for this rule. A request must satisfy all conditions
    /// in at least one match to be routed by this rule.
    /// </summary>
    [YamlMember(Alias = "matches", Order = 1)]
    public List<HttpRouteMatchV1> Matches { get; } = [];

    /// <summary>
    /// Gets the filters applied to requests matched by this rule. Filters run in the order they are
    /// declared, before the request is forwarded to a backend (for example an <c>URLRewrite</c> filter
    /// that rewrites the path).
    /// </summary>
    /// <remarks>
    /// Kubernetes parses manifests into typed objects, so mapping key order carries no semantic meaning
    /// to a controller. The explicit <c>Order</c> values exist because the YAML serializer otherwise
    /// falls back to reflection order, which .NET does not contractually guarantee: pinning the order
    /// keeps generated manifests deterministic (and their snapshots stable) and matches the field order
    /// used by the Gateway API reference documentation, which makes rendered charts easier to read. See
    /// <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#filters-optional"/>.
    /// </remarks>
    [YamlMember(Alias = "filters", Order = 2)]
    public List<HttpRouteFilterV1> Filters { get; } = [];

    /// <summary>
    /// Gets the backend references that matched requests are forwarded to.
    /// </summary>
    [YamlMember(Alias = "backendRefs", Order = 3)]
    public List<HttpRouteBackendRefV1> BackendRefs { get; } = [];
}

/// <summary>
/// Defines match conditions for an HTTPRoute rule.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteMatchV1
{
    /// <summary>
    /// Gets or sets the path match condition.
    /// </summary>
    [YamlMember(Alias = "path")]
    public HttpRoutePathMatchV1? Path { get; set; }

    /// <summary>
    /// Gets the header match conditions.
    /// </summary>
    [YamlMember(Alias = "headers")]
    public List<HttpRouteHeaderMatchV1> Headers { get; } = [];
}

/// <summary>
/// Defines a path match condition for an HTTPRoute rule.
/// </summary>
[YamlSerializable]
public sealed class HttpRoutePathMatchV1
{
    /// <summary>
    /// Gets or sets the type of path matching. Values: <c>"PathPrefix"</c>, <c>"Exact"</c>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "PathPrefix";

    /// <summary>
    /// Gets or sets the path value to match.
    /// </summary>
    [YamlMember(Alias = "value")]
    public string Value { get; set; } = null!;
}

/// <summary>
/// Defines a header match condition for an HTTPRoute rule.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteHeaderMatchV1
{
    /// <summary>
    /// Gets or sets the match type. Values: <c>"Exact"</c>, <c>"RegularExpression"</c>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "Exact";

    /// <summary>
    /// Gets or sets the header name.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the header value to match.
    /// </summary>
    [YamlMember(Alias = "value")]
    public string Value { get; set; } = null!;
}

/// <summary>
/// A reference to a backend service that receives matched traffic.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteBackendRefV1
{
    /// <summary>
    /// Gets or sets the name of the Kubernetes Service.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the port number on the service.
    /// </summary>
    [YamlMember(Alias = "port")]
    public int Port { get; set; }
}

/// <summary>
/// A filter applied to requests matched by an HTTPRoute rule. Filters transform or redirect a
/// request before it is forwarded to a backend.
/// </summary>
/// <remarks>
/// The <see cref="Type"/> selects which filter runs and determines which companion property is
/// populated (for example <see cref="UrlRewrite"/> for <c>URLRewrite</c>). See
/// <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#filters-optional"/>.
/// </remarks>
[YamlSerializable]
public sealed class HttpRouteFilterV1
{
    /// <summary>
    /// Gets or sets the filter type. The Gateway API defines <c>RequestHeaderModifier</c> and
    /// <c>RequestRedirect</c> at Core support level, <c>ResponseHeaderModifier</c>, <c>RequestMirror</c>,
    /// and <c>URLRewrite</c> at Extended support level, and <c>ExtensionRef</c> as
    /// implementation-specific. Confirm your controller supports the chosen type.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = null!;

    /// <summary>
    /// Gets or sets the URL rewrite configuration. Populated only when <see cref="Type"/> is
    /// <c>URLRewrite</c>.
    /// </summary>
    [YamlMember(Alias = "urlRewrite")]
    public HttpUrlRewriteFilterV1? UrlRewrite { get; set; }
}

/// <summary>
/// Configuration for a <c>URLRewrite</c> HTTPRoute filter, which rewrites parts of a request URL
/// before forwarding it to a backend.
/// </summary>
/// <remarks>
/// See <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#httpurlrewritefilter"/>.
/// </remarks>
[YamlSerializable]
public sealed class HttpUrlRewriteFilterV1
{
    /// <summary>
    /// Gets or sets the hostname to rewrite the request's <c>Host</c> header to. When
    /// <see langword="null"/> the host is left unchanged.
    /// </summary>
    [YamlMember(Alias = "hostname")]
    public string? Hostname { get; set; }

    /// <summary>
    /// Gets or sets the path modification applied to the request. When <see langword="null"/> the
    /// path is left unchanged.
    /// </summary>
    [YamlMember(Alias = "path")]
    public HttpPathModifierV1? Path { get; set; }
}

/// <summary>
/// Describes how a request path is modified by a <c>URLRewrite</c> (or redirect) HTTPRoute filter.
/// </summary>
/// <remarks>
/// See <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#httppathmodifier"/>.
/// </remarks>
[YamlSerializable]
public sealed class HttpPathModifierV1
{
    /// <summary>
    /// Gets or sets the modifier type. Values: <c>"ReplacePrefixMatch"</c> (replace the portion of
    /// the path matched by the rule's <c>PathPrefix</c> match) or <c>"ReplaceFullPath"</c> (replace
    /// the entire path).
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = null!;

    /// <summary>
    /// Gets or sets the value that replaces the matched path prefix. Populated only when
    /// <see cref="Type"/> is <c>ReplacePrefixMatch</c>.
    /// </summary>
    [YamlMember(Alias = "replacePrefixMatch")]
    public string? ReplacePrefixMatch { get; set; }

    /// <summary>
    /// Gets or sets the value that replaces the entire path. Populated only when <see cref="Type"/>
    /// is <c>ReplaceFullPath</c>.
    /// </summary>
    [YamlMember(Alias = "replaceFullPath")]
    public string? ReplaceFullPath { get; set; }
}
