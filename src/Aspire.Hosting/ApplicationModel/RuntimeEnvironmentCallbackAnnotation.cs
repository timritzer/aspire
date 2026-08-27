// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Identifies environment callbacks that populate runtime-only reference data.
/// </summary>
internal sealed class RuntimeEnvironmentCallbackAnnotation : EnvironmentCallbackAnnotation
{
    public RuntimeEnvironmentCallbackAnnotation(Action<EnvironmentCallbackContext> callback)
        : base(callback)
    {
    }

    public RuntimeEnvironmentCallbackAnnotation(Func<EnvironmentCallbackContext, Task> callback)
        : base(callback)
    {
    }
}
