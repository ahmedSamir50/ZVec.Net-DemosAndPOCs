using MudBlazor;

namespace MovieRecs.Maui.Components.Layout;

public partial class MainLayout
{
    private readonly MudTheme _theme = new()
    {
        PaletteDark = new PaletteDark
        {
            Black = "#050508",
            Background = "#0B0B0F",
            Surface = "#16161D",
            Primary = "#E50914",
            Secondary = "#F5F5F1",
            AppbarBackground = "#0B0B0F",
            AppbarText = "#F5F5F1",
            TextPrimary = "#F5F5F1",
            TextSecondary = "#A3A3A8",
            DrawerBackground = "#0B0B0F",
            ActionDefault = "#F5F5F1",
            Divider = "#2A2A32"
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Segoe UI", "Helvetica Neue", "Arial", "sans-serif"]
            },
            H5 = new H5Typography
            {
                FontFamily = ["Georgia", "Times New Roman", "serif"],
                FontWeight = "700"
            }
        }
    };
}
