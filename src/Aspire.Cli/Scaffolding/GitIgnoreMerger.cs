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
            .Select(NormalizeRootedEntry)
            .ToHashSet(StringComparer.Ordinal);

        var missingEntries = ReadEntries(scaffoldContent)
            .Where(entry => !existingEntries.Contains(entry)
                && !ContainsEquivalentEntry(existingNormalized, entry))
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

    private static bool ContainsEquivalentEntry(HashSet<string> existingEntries, string scaffoldEntry)
    {
        var normalizedScaffoldEntry = NormalizeRootedEntry(scaffoldEntry);
        if (existingEntries.Contains(normalizedScaffoldEntry))
        {
            return true;
        }

        // A slashless pattern matches both a file and a directory, so it already covers a
        // generated directory-only pattern. The inverse is not true: "foo/" does not cover
        // a generated "foo" entry because it would leave a file named "foo" unignored.
        return normalizedScaffoldEntry.EndsWith('/') &&
            existingEntries.Contains(normalizedScaffoldEntry.TrimEnd('/'));
    }

    // A rooted pattern is sufficient when the scaffold only needs to ignore the repository-root entry.
    private static string NormalizeRootedEntry(string entry)
        => entry.StartsWith('/') ? entry[1..] : entry;
}
