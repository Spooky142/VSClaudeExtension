using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeForVS.Agent;

/// <summary>An image sent with a prompt (pasted or dropped into the composer).</summary>
internal sealed record PromptImage(string MediaType, byte[] Data)
{
    /// <summary>Largest image the API accepts in base64 form is 5 MB; keep the raw bytes under that.</summary>
    public const int MaxBytes = 3_750_000;

    /// <summary>The API's media type for an image file, or null if it isn't a supported image.</summary>
    public static string? MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };

    public string Base64 => Convert.ToBase64String(this.Data);
}

/// <summary>What gets sent to Claude for one user turn: the text (with any attached context) and images.</summary>
internal sealed record ChatPrompt(string Text, IReadOnlyList<PromptImage> Images)
{
    public ChatPrompt(string text)
        : this(text, [])
    {
    }
}
