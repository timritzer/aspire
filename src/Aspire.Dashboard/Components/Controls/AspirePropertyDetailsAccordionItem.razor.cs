// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class AspirePropertyDetailsAccordionItem
{
    /// <summary>
    /// Gets or sets the section header.
    /// </summary>
    [Parameter, EditorRequired]
    public required string Header { get; set; }

    /// <summary>
    /// Gets or sets the number of items in the section.
    /// </summary>
    [Parameter]
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the section is expanded.
    /// </summary>
    [Parameter]
    public bool Expanded { get; set; }

    /// <summary>
    /// Gets or sets the section content.
    /// </summary>
    [Parameter, EditorRequired]
    public required RenderFragment ChildContent { get; set; }
}