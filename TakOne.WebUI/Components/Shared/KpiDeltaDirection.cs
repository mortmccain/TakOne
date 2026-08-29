// CA1716: the Components/Shared folder's generated namespace (shared by
// BottomNav.razor etc.) predates this file — renaming it now would churn
// every consumer for zero behavioral gain.
#pragma warning disable CA1716

namespace TakOne.WebUI.Components.Shared;

#pragma warning restore CA1716

/// <summary>
/// The trend direction of a KPI delta chip (Round 4) — picks the arrow
/// glyph and the semantic color class. Up = success (this is a sales
/// dashboard: higher counts/revenue read as "good"), Down = danger,
/// Flat = secondary.
/// </summary>
public enum KpiDeltaDirection
{
    /// <summary>Current period is higher — up arrow, success color.</summary>
    Up = 1,

    /// <summary>Current period is lower — down arrow, danger color.</summary>
    Down = 2,

    /// <summary>No change (or informational) — dash glyph, muted color.</summary>
    Flat = 3
}
