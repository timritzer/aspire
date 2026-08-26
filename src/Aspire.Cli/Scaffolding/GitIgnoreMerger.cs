// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Merges scaffold-generated <c>.gitignore</c> entries with existing content.
/// </summary>
internal static class GitIgnoreMerger
{
    /// <summary>
    /// Appends missing scaffold entries while preserving existing content and line endings.
    /// </summary>
    internal static string Merge(string existingContent, string scaffoldContent)
    {
        ArgumentNullException.ThrowIfNull(existingContent);
        ArgumentNullException.ThrowIfNull(scaffoldContent);

        if (string.IsNullOrEmpty(existingContent))
        {
            return scaffoldContent;
        }

        var existingEntries = ReadEntries(existingContent).ToHashSet(StringComparer.Ordinal);
        var existingNormalized = existingEntries
            .Select(NormalizeEntry)
            .ToHashSet(StringComparer.Ordinal);

        var missingEntries = ReadEntries(scaffoldContent)
            .Where(entry => !existingEntries.Contains(entry)
                && !existingNormalized.Contains(NormalizeEntry(entry)))
            .ToArray();

        if (missingEntries.Length == 0)
        {
            return existingContent;
        }

        var newline = existingContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var mergedContent = existingContent;
        if (!mergedContent.EndsWith('\n'))
        {
            mergedContent += newline;
        }

        return mergedContent + string.Join(newline, missingEntries) + newline;
    }

    private static IEnumerable<string> ReadEntries(string content)
    {
        // Entries are line-oriented, for example:
        //   node_modules/
        //   /.aspire/
        // Blank lines and trailing whitespace do not participate in duplicate detection,
        // while the original content remains unchanged in the merged result.
        using var reader = new StringReader(content);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.TrimEnd();
            }
        }
    }

    // Treat rooted (`/foo/`) and unrooted (`foo/`) forms as equivalent when deciding
    // whether a scaffold entry needs to be appended.
    private static string NormalizeEntry(string entry)
        => entry.StartsWith('/') ? entry[1..] : entry;
}
