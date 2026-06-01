using BlueBubbles.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlueBubbles.Windows.ViewModels;

/// <summary>Render state of a link card.</summary>
public enum UrlPreviewState
{
    /// <summary>Has a title and/or image — show the full card.</summary>
    Rich,
    /// <summary>A bare link with no server preview — show a "Show preview" affordance.</summary>
    NeedsPreview,
    /// <summary>A metadata fetch is in flight.</summary>
    Loading,
    /// <summary>No preview could be produced — show a minimal generic card that still opens the link.</summary>
    Generic
}

/// <summary>
/// Data + behaviour for a rich link (URL) preview card. For server-enriched links the title/summary/
/// site and a hero <see cref="Image"/> (the iMessage <c>pluginPayloadAttachment</c>) are populated up
/// front (<see cref="UrlPreviewState.Rich"/>). For a bare link it starts in
/// <see cref="UrlPreviewState.NeedsPreview"/>; <see cref="LoadPreviewCommand"/> fetches Open-Graph
/// metadata on demand via <see cref="Fetcher"/> (wired by the chat VM), upgrading to Rich or falling
/// back to <see cref="UrlPreviewState.Generic"/>.
/// </summary>
public partial class UrlPreviewViewModel : ObservableObject
{
    public string Url { get; }

    /// <summary>Apple-supplied hero image delivered as a local attachment (rich server previews).</summary>
    public AttachmentViewModel? Image { get; }

    [ObservableProperty] public partial string? Title { get; set; }
    [ObservableProperty] public partial string? Summary { get; set; }
    [ObservableProperty] public partial string? SiteName { get; set; }

    /// <summary>Remote hero image URL discovered by an on-demand metadata fetch.</summary>
    [ObservableProperty] public partial string? ImageUri { get; set; }

    [ObservableProperty] public partial UrlPreviewState State { get; set; }

    /// <summary>Fetches page metadata for <see cref="LoadPreviewCommand"/>. Wired by the chat VM from
    /// <c>ILinkPreviewService</c>; null in non-DI contexts (e.g. tests), which simply disables fetching.</summary>
    public Func<string, CancellationToken, Task<LinkMetadata?>>? Fetcher { get; set; }

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : Url;

    /// <summary>Title to display, falling back to the site name and finally the host.</summary>
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title!
        : !string.IsNullOrWhiteSpace(SiteName) ? SiteName!
        : Host;

    public UrlPreviewViewModel(string url, string? title, string? summary, string? siteName,
        AttachmentViewModel? image, UrlPreviewState state)
    {
        Url = url;
        Title = title;
        Summary = summary;
        SiteName = siteName;
        Image = image;
        State = state;
    }

    /// <summary>User-triggered ("Show preview"): fetch page metadata and upgrade the card, or fall
    /// back to a generic card if nothing usable comes back.</summary>
    [RelayCommand]
    private async Task LoadPreviewAsync()
    {
        if (Fetcher is null || State is UrlPreviewState.Loading or UrlPreviewState.Rich) return;

        State = UrlPreviewState.Loading;
        try
        {
            var meta = await Fetcher(Url, CancellationToken.None);
            if (meta is not null && (!string.IsNullOrWhiteSpace(meta.Title) || !string.IsNullOrWhiteSpace(meta.ImageUrl)))
            {
                if (!string.IsNullOrWhiteSpace(meta.Title)) Title = meta.Title;
                if (!string.IsNullOrWhiteSpace(meta.Description)) Summary = meta.Description;
                if (!string.IsNullOrWhiteSpace(meta.SiteName)) SiteName = meta.SiteName;
                ImageUri = meta.ImageUrl;
                State = UrlPreviewState.Rich;
            }
            else
            {
                State = UrlPreviewState.Generic;
            }
        }
        catch
        {
            State = UrlPreviewState.Generic;
        }
    }
}
