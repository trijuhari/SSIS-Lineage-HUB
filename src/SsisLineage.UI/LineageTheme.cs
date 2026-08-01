using MudBlazor;

namespace SsisLineage.UI;

/// <summary>
/// Shared MudBlazor theme for the SSIS Lineage UI. The dark palette mirrors the
/// original slate/sky design; the light palette is a clean neutral counterpart.
/// </summary>
public static class LineageTheme
{
    public static readonly MudTheme Theme = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0284c7",       // sky-600
            Secondary = "#7c3aed",     // violet-600
            Success = "#16a34a",
            Warning = "#d97706",
            Error = "#dc2626",
            Info = "#0ea5e9",
            Background = "#f1f5f9",
            BackgroundGray = "#e2e8f0",
            Surface = "#ffffff",
            AppbarBackground = "#0f172a",
            AppbarText = "#f8fafc",
            DrawerBackground = "#ffffff",
            TextPrimary = "#1e293b",
            TextSecondary = "#64748b",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#fafafa",       // clean white
            Secondary = "#a1a1aa",     // zinc-400
            Success = "#10b981",
            Warning = "#f59e0b",
            Error = "#ef4444",
            Info = "#3b82f6",
            Background = "#09090b",     // zinc-950
            BackgroundGray = "#18181b", // zinc-900
            Surface = "#09090b",        // zinc-950
            AppbarBackground = "#09090b",
            AppbarText = "#fafafa",
            DrawerBackground = "#09090b",
            TextPrimary = "#fafafa",
            TextSecondary = "#a1a1aa",
            Tertiary = "#d4d4d8",
            ActionDefault = "#a1a1aa",
            DrawerText = "#a1a1aa",
            LinesDefault = "rgba(255,255,255,0.1)",
            TableLines = "rgba(255,255,255,0.1)",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerWidthLeft = "240px",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = new[] { "Segoe UI", "Inter", "Roboto", "Helvetica", "Arial", "sans-serif" } }
        }
    };
}
