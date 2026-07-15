// =============================================================================
//  UtmLinks — single source of truth for the tracked-link format.
//  Used by IrisEngine.BuildCaption and the outbox creator path so the query
//  string is never duplicated. utm_source=iris / utm_medium=organic are neutral
//  pre-format defaults: PlatformFormatter rewrites them per platform variant
//  (utm_source=<platform>, utm_medium=manual) before anything is posted.
// =============================================================================
namespace InnerShiftLab.Core;

public static class UtmLinks
{
    public static string BuildTracked(string baseUrl, string campaign, string content)
        => $"{baseUrl}?utm_source=iris&utm_medium=organic" +
           $"&utm_campaign={Uri.EscapeDataString(campaign)}" +
           $"&utm_content={Uri.EscapeDataString(content)}&utm_term=iris";
}
