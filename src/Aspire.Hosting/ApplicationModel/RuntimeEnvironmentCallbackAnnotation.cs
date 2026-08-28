// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an environment callback that populates values needed only when a resource runs.
/// </summary>
/// <remarks>
/// Language integrations use this annotation to distinguish runtime reference data, such as
/// connection strings and service-discovery endpoints, from environment values that must also
/// be available to build tools.
/// </remarks>
[Experimental("ASPIREENVIRONMENT001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class RuntimeEnvironmentCallbackAnnotation : EnvironmentCallbackAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeEnvironmentCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The callback that populates runtime environment variables.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is null.</exception>
    public RuntimeEnvironmentCallbackAnnotation(Action<EnvironmentCallbackContext> callback)
        : base(callback)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeEnvironmentCallbackAnnotation"/> class.
    /// </summary>
    /// <param name="callback">The callback that asynchronously populates runtime environment variables.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="callback"/> is null.</exception>
    public RuntimeEnvironmentCallbackAnnotation(Func<EnvironmentCallbackContext, Task> callback)
        : base(callback)
    {
    }
}
