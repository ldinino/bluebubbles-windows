namespace BlueBubbles.Core.Models;

/// <summary>Open-Graph / HTML metadata scraped from a web page to build a link preview when the
/// server didn't supply one (the user-triggered "Show preview" path).</summary>
public sealed record LinkMetadata(
    string? Title,
    string? Description,
    string? ImageUrl,
    string? SiteName);
