// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring Kubernetes Gateway API resources in the Aspire application model.
/// </summary>
public static class KubernetesGatewayExtensions
{
    /// <summary>
    /// Adds a Kubernetes Gateway API Gateway resource to the application model as a child of the specified
    /// Kubernetes environment. The gateway generates a <c>gateway.networking.k8s.io/v1 Gateway</c> resource
    /// and one or more <c>HTTPRoute</c> resources in the Helm chart output at publish time.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the gateway resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <example>
    /// <code>
    /// var k8s = builder.AddKubernetesEnvironment("k8s");
    /// var gateway = k8s.AddGateway("public")
    ///     .WithGatewayClass("azure-alb-external");
    ///
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// gateway.WithRoute("/api", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesGatewayResource> AddGateway(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var gateway = new KubernetesGatewayResource(name, builder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(gateway);
        }

        return builder.ApplicationBuilder.AddResource(gateway)
            .WithIconName("GlobeArrowForward")
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Configures the gateway to attach its routes to a pre-existing, platform-owned Gateway rather
    /// than generating a new <c>gateway.networking.k8s.io/v1 Gateway</c> object. Aspire emits only the
    /// gateway's <c>HTTPRoute</c> resources, whose <c>parentRefs</c> target the named Gateway.
    /// </summary>
    /// <remarks>
    /// This mirrors the <c>AsExisting</c> pattern used elsewhere in Aspire for referencing infrastructure
    /// that lives outside the deployment. Because the referenced Gateway is owned by the platform, every
    /// builder call that shapes the <c>Gateway</c> object itself is ignored — its listeners, TLS,
    /// <c>gatewayClassName</c>, annotations, and <c>allowedRoutes</c> are all managed externally. That
    /// covers <see cref="WithGatewayClass(IResourceBuilder{KubernetesGatewayResource}, string)"/>,
    /// <see cref="WithTls(IResourceBuilder{KubernetesGatewayResource}, string)"/>,
    /// <see cref="WithGatewayAnnotation(IResourceBuilder{KubernetesGatewayResource}, string, string)"/>,
    /// cert-manager's <c>WithTls(issuer)</c> cluster-issuer wiring, and provider-specific load-balancer
    /// configuration such as Azure Application Gateway for Containers. A warning is emitted during
    /// publishing when any of them is combined with this method. Route-level configuration continues to
    /// apply, because it shapes the <c>HTTPRoute</c> rather than the Gateway: see
    /// <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/> and <c>WithRoute</c>.
    /// Supplying a <paramref name="namespace"/> lets a route attach to a Gateway in a different namespace,
    /// and a <paramref name="sectionName"/> targets a specific listener. See the Gateway API documentation:
    /// <see href="https://gateway-api.sigs.k8s.io/api-types/httproute/#attaching-to-gateways"/>.
    /// </remarks>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="name">The <c>metadata.name</c> of the existing Gateway object.</param>
    /// <param name="namespace">The namespace of the existing Gateway. When <see langword="null"/> the reference resolves within the deployment's namespace.</param>
    /// <param name="sectionName">The listener (section) name on the existing Gateway to attach to. When <see langword="null"/> routes attach to every compatible listener.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the union-based asExisting dispatcher export.")]
    public static IResourceBuilder<KubernetesGatewayResource> AsExisting(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string name,
        string? @namespace = null,
        string? sectionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Empty strings are rejected rather than ignored: the YAML emitter drops empty scalars, so an
        // empty namespace or sectionName would silently produce a bare parentRef that attaches to the
        // wrong Gateway instead of failing loudly here.
        if (@namespace is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        }

        if (sectionName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        }

        return AsExistingCore(
            builder,
            ReferenceExpression.Create($"{name}"),
            @namespace is null ? null : ReferenceExpression.Create($"{@namespace}"),
            sectionName is null ? null : ReferenceExpression.Create($"{sectionName}"));
    }

    /// <summary>
    /// Configures the gateway to attach its routes to a pre-existing, platform-owned Gateway identified
    /// by parameters that are resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="name">A parameter resource builder for the <c>metadata.name</c> of the existing Gateway object.</param>
    /// <param name="namespace">A parameter resource builder for the namespace of the existing Gateway, or <see langword="null"/> to resolve within the deployment's namespace.</param>
    /// <param name="sectionName">A parameter resource builder for the listener (section) name, or <see langword="null"/> to attach to every compatible listener.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the union-based asExisting dispatcher export.")]
    public static IResourceBuilder<KubernetesGatewayResource> AsExisting(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> name,
        IResourceBuilder<ParameterResource>? @namespace = null,
        IResourceBuilder<ParameterResource>? sectionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return AsExistingCore(
            builder,
            ReferenceExpression.Create($"{name.Resource}"),
            @namespace is null ? null : ReferenceExpression.Create($"{@namespace.Resource}"),
            sectionName is null ? null : ReferenceExpression.Create($"{sectionName.Resource}"));
    }

    /// <summary>
    /// Configures the gateway to attach its routes to a pre-existing, platform-owned Gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="name">The <c>metadata.name</c> of the existing Gateway as a string or parameter resource builder.</param>
    /// <param name="namespace">The namespace of the existing Gateway as a string or parameter resource builder.</param>
    /// <param name="sectionName">The listener (section) name as a string or parameter resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport(MethodName = "asExisting")]
    internal static IResourceBuilder<KubernetesGatewayResource> AsExisting(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object name,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object? @namespace = null,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object? sectionName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        return AsExistingCore(
            builder,
            ToGatewayReference(name, nameof(name))!,
            ToGatewayReference(@namespace, nameof(@namespace)),
            ToGatewayReference(sectionName, nameof(sectionName)));
    }

    private static void ValidateRewritePrefix(string? rewritePrefix, GatewayPathMatchType pathType, string paramName)
    {
        if (rewritePrefix is null)
        {
            return;
        }

        // An empty or whitespace value cannot simply be ignored: the YAML emitter drops empty scalars,
        // which would render "type: ReplacePrefixMatch" with no "replacePrefixMatch" sibling and be
        // rejected by the HTTPRoute CRD.
        ArgumentException.ThrowIfNullOrWhiteSpace(rewritePrefix, paramName);

        if (!rewritePrefix.StartsWith('/'))
        {
            throw new ArgumentException("Rewrite prefix must start with '/'.", paramName);
        }

        // ReplacePrefixMatch substitutes the portion of the path that the match consumed, so there has to
        // be a matched prefix to replace. With an Exact or RegularExpression match there is none.
        if (pathType != GatewayPathMatchType.PathPrefix)
        {
            throw new ArgumentException(
                $"Rewrite prefix requires a {nameof(GatewayPathMatchType.PathPrefix)} path match; " +
                $"'{pathType}' does not match a prefix that can be replaced.",
                paramName);
        }
    }

    private static ReferenceExpression? ToGatewayReference(object? value, string paramName) => value switch
    {
        null => null,
        string text => string.IsNullOrWhiteSpace(text)
            ? throw new ArgumentException("Value must not be empty or whitespace.", paramName)
            : ReferenceExpression.Create($"{text}"),
        IResourceBuilder<ParameterResource> parameter => ReferenceExpression.Create($"{parameter.Resource}"),
        _ => throw new ArgumentException("Value must be a string or a parameter resource builder.", paramName)
    };

    private static IResourceBuilder<KubernetesGatewayResource> AsExistingCore(
        IResourceBuilder<KubernetesGatewayResource> builder,
        ReferenceExpression name,
        ReferenceExpression? @namespace,
        ReferenceExpression? sectionName)
    {
        // Replace rather than append so repeated calls are idempotent and the last one wins, matching
        // how the other single-valued Kubernetes annotations in this package are applied.
        return builder.WithAnnotation(
            new ExistingKubernetesGatewayAnnotation(name, @namespace, sectionName),
            ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Sets the GatewayClass name that selects which controller implementation handles this gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="className">The GatewayClass name (e.g., <c>"azure-alb-external"</c>, <c>"istio"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayClass(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(className);

        builder.Resource.GatewayClassName = ReferenceExpression.Create($"{className}");
        return builder;
    }

    /// <summary>
    /// Sets the GatewayClass name using a parameter that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="className">A parameter resource builder for the GatewayClass name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayClassParam")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayClass(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(className);

        builder.Resource.GatewayClassName = ReferenceExpression.Create($"{className.Resource}");
        return builder;
    }

    /// <summary>
    /// Adds a path-based routing rule to the gateway. The rule matches each hostname configured
    /// with <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/>, or all
    /// hosts when no hostname is configured, and routes matching traffic to the endpoint's backing
    /// Kubernetes service. This generates an <c>HTTPRoute</c> resource attached to the Gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="GatewayPathMatchType.PathPrefix"/>.</param>
    /// <param name="rewritePrefix">
    /// When set, rewrites the matched path prefix to this value before the request reaches the backend
    /// (e.g. a route at <c>"/my-app"</c> with <paramref name="rewritePrefix"/> <c>"/"</c> presents the
    /// backend with <c>"/"</c>). Emitted as a Gateway API <c>URLRewrite</c> filter with a
    /// <c>ReplacePrefixMatch</c> path modifier.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayPathRoute")]
    public static IResourceBuilder<KubernetesGatewayResource> WithRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string path,
        EndpointReference endpoint,
        GatewayPathMatchType pathType = GatewayPathMatchType.PathPrefix,
        string? rewritePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must start with '/'.", nameof(path));
        }

        ValidateRewritePrefix(rewritePrefix, pathType, nameof(rewritePrefix));

        builder.Resource.Routes.Add(new GatewayRouteConfig(
            Host: null,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint,
            RewritePrefix: rewritePrefix));

        return builder;
    }

    /// <summary>
    /// Adds a host-and-path-based routing rule to the gateway. The rule matches traffic for
    /// the specified host and path, routing it to the given endpoint's backing Kubernetes service.
    /// This generates an <c>HTTPRoute</c> resource with a <c>hostnames</c> filter.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="host">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <param name="path">The URL path to match. Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="GatewayPathMatchType.PathPrefix"/>.</param>
    /// <param name="rewritePrefix">
    /// When set, rewrites the matched path prefix to this value before the request reaches the backend
    /// (e.g. a route at <c>"/my-app"</c> with <paramref name="rewritePrefix"/> <c>"/"</c> presents the
    /// backend with <c>"/"</c>). Emitted as a Gateway API <c>URLRewrite</c> filter with a
    /// <c>ReplacePrefixMatch</c> path modifier.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostRoute")]
    public static IResourceBuilder<KubernetesGatewayResource> WithRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string host,
        string path,
        EndpointReference endpoint,
        GatewayPathMatchType pathType = GatewayPathMatchType.PathPrefix,
        string? rewritePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must start with '/'.", nameof(path));
        }

        ValidateRewritePrefix(rewritePrefix, pathType, nameof(rewritePrefix));

        builder.Resource.Routes.Add(new GatewayRouteConfig(
            Host: host,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint,
            RewritePrefix: rewritePrefix));

        return builder;
    }

    /// <summary>
    /// Adds a hostname that this gateway's routes match. Multiple hostnames can be added by calling
    /// this method repeatedly. Routes without an explicit host apply to each configured hostname.
    /// Hostnames are used as <c>hostnames</c> in generated <c>HTTPRoute</c> resources and as HTTPS
    /// listener hostnames when TLS is configured.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="hostname">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostname", MethodName = "withHostname")]
    public static IResourceBuilder<KubernetesGatewayResource> WithHostname(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string hostname)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        builder.Resource.Hostnames.Add(ReferenceExpression.Create($"{hostname}"));
        return builder;
    }

    /// <summary>
    /// Adds a hostname using a parameter that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="hostname">A parameter resource builder for the hostname value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostnameParam")]
    public static IResourceBuilder<KubernetesGatewayResource> WithHostname(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> hostname)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hostname);

        builder.Resource.Hostnames.Add(ReferenceExpression.Create($"{hostname.Resource}"));
        return builder;
    }

    /// <summary>
    /// Configures TLS termination on the gateway by adding an HTTPS listener that references
    /// a Kubernetes TLS secret. The Gateway terminates TLS and forwards plain HTTP to backends.
    /// This does not create a separate route — existing HTTPRoutes serve both HTTP and HTTPS.
    /// The TLS configuration applies to all hostnames configured via <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/>.
    /// </summary>
    /// <ats-summary>Configures TLS on a Kubernetes Gateway listener</ats-summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">The name of the Kubernetes <c>kubernetes.io/tls</c> Secret.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayTls", MethodName = "withTls")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(secretName);

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName}")));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination using a parameter for the secret name.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">A parameter resource builder for the secret name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayTlsParam")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secretName);

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName.Resource}")));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination with an auto-generated secret name derived from the gateway name.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayTlsAuto")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var secretName = $"{builder.Resource.Name}-tls";

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName}")));

        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation to the generated Gateway resource.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// This sets Kubernetes <c>metadata.annotations</c> on the generated K8S Gateway resource,
    /// not Aspire <see cref="ApplicationModel.IResourceAnnotation"/> instances. These are key-value
    /// string pairs used by ingress controllers for provider-specific configuration.
    /// </para>
    /// <para>
    /// For Azure Application Gateway for Containers (AGC), you typically need:
    /// <c>alb.networking.azure.io/alb-name</c> and <c>alb.networking.azure.io/alb-namespace</c>.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayAnnotation(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.GatewayAnnotations[key] = ReferenceExpression.Create($"{value}");
        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation with a parameter value that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">A parameter resource builder for the annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayAnnotationParam")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayAnnotation(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string key,
        IResourceBuilder<ParameterResource> value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.GatewayAnnotations[key] = ReferenceExpression.Create($"{value.Resource}");
        return builder;
    }
}
