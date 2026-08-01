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
            Primary = "#38bdf8",       // sky-400
            Secondary = "#a78bfa",     // violet-400
            Success = "#10b981",
            Warning = "#f59e0b",
            Error = "#ef4444",
            Info = "#3b82f6",
            Background = "#020617",     // slate-950
            BackgroundGray = "#0f172a", // slate-900
            Surface = "#020617",        // slate-950
            AppbarBackground = "#020617",
            AppbarText = "#f8fafc",
            DrawerBackground = "#020617",
            TextPrimary = "#f8fafc",
            TextSecondary = "#94a3b8",
            Tertiary = "#cbd5e1",
            ActionDefault = "#94a3b8",
            DrawerText = "#94a3b8",
            LinesDefault = "rgba(255,255,255,0.08)",
            TableLines = "rgba(255,255,255,0.08)",
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
