// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Cli.Agents;

internal enum AgentAssetFileComparison
{
    ExactBytes,
    NormalizedUtf8Text,
}

/// <summary>
/// Represents a validated file contained in an agent asset.
/// </summary>
internal sealed class AgentAssetFile
{
    private static readonly UTF8Encoding s_strictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentAssetFile(string relativePath, string content)
        : this(
            relativePath,
            Encoding.UTF8.GetBytes(content),
            AgentAssetFileComparison.NormalizedUtf8Text)
    {
    }

    public AgentAssetFile(
        string relativePath,
        ReadOnlySpan<byte> content,
        AgentAssetFileComparison comparison)
    {
        RelativePath = relativePath;
        Bytes = content.ToArray();
        Comparison = comparison;
    }

    public string RelativePath { get; }

    public ReadOnlyMemory<byte> Bytes { get; }

    public string Content => GetTextContent();

    public AgentAssetFileComparison Comparison { get; }

    public string GetTextContent()
    {
        return DecodeText(Bytes.Span);
    }

    public bool ContentEquals(ReadOnlySpan<byte> existingContent)
    {
        if (Comparison is AgentAssetFileComparison.ExactBytes)
        {
            return Bytes.Span.SequenceEqual(existingContent);
        }

        try
        {
            return string.Equals(
                GetTextContent().ReplaceLineEndings("\n"),
                DecodeText(existingContent).ReplaceLineEndings("\n"),
                StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static string DecodeText(ReadOnlySpan<byte> content)
    {
        var text = s_strictUtf8.GetString(content);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
