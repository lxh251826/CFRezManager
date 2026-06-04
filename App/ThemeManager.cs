using System.Windows.Media;

namespace CFRezManager;

internal static class ThemeManager
{
    public static AppTheme Parse(string? value)
    {
        return string.Equals(value, "Dark", StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Dark
            : AppTheme.Light;
    }

    public static string ToSettingsValue(AppTheme theme)
    {
        return theme == AppTheme.Dark ? "Dark" : "Light";
    }

    public static void ApplySavedTheme()
    {
        Apply(Parse(UserSettings.Load().Theme));
    }

    public static void Apply(AppTheme theme)
    {
        System.Windows.ResourceDictionary resources = System.Windows.Application.Current.Resources;

        if (theme == AppTheme.Dark)
        {
            SetBrush(resources, "AppWindowBrush", "#171A1D");
            SetBrush(resources, "AppSurfaceBrush", "#1F2429");
            SetBrush(resources, "AppSurfaceAltBrush", "#292F36");
            SetBrush(resources, "AppInputBrush", "#121518");
            SetBrush(resources, "AppBorderBrush", "#3B454E");
            SetBrush(resources, "AppButtonBrush", "#252B32");
            SetBrush(resources, "AppButtonHoverBrush", "#303842");
            SetBrush(resources, "AppButtonPressedBrush", "#3A4450");
            SetBrush(resources, "AppButtonBorderBrush", "#46515D");
            SetBrush(resources, "AppTextBrush", "#F3F4F6");
            SetBrush(resources, "AppMutedTextBrush", "#C5CCD5");
            SetBrush(resources, "AppSubtleTextBrush", "#A5AFBC");
            SetBrush(resources, "AppItemHoverBrush", "#2A3138");
            SetBrush(resources, "AppItemHoverBorderBrush", "#4B5662");
            SetBrush(resources, "AppSelectionBrush", "#294C63");
            SetBrush(resources, "AppSelectionHoverBrush", "#335E78");
            SetBrush(resources, "AppSelectionBorderBrush", "#73B6D6");
            SetBrush(resources, "AppFilePageBrush", "#F8FAFC");
            SetBrush(resources, "AppFileFoldBrush", "#CBD5E1");
            SetBrush(resources, "AppThumbnailBackgroundBrush", "#15191D");
            SetBrush(resources, "AppBadgeBrush", "#D9111827");
            SetBrush(resources, "AppSelectionOverlayBrush", "#334F83D1");
            SetBrush(resources, "AppAccentBrush", "#73B6D6");
            return;
        }

        SetBrush(resources, "AppWindowBrush", "#F8FAFC");
        SetBrush(resources, "AppSurfaceBrush", "#FFFFFF");
        SetBrush(resources, "AppSurfaceAltBrush", "#F5F7FA");
        SetBrush(resources, "AppInputBrush", "#FFFFFF");
        SetBrush(resources, "AppBorderBrush", "#D7DCE2");
        SetBrush(resources, "AppButtonBrush", "#FFFFFF");
        SetBrush(resources, "AppButtonHoverBrush", "#F1F5F9");
        SetBrush(resources, "AppButtonPressedBrush", "#E5E7EB");
        SetBrush(resources, "AppButtonBorderBrush", "#CBD5E1");
        SetBrush(resources, "AppTextBrush", "#111827");
        SetBrush(resources, "AppMutedTextBrush", "#4B5563");
        SetBrush(resources, "AppSubtleTextBrush", "#6B7280");
        SetBrush(resources, "AppItemHoverBrush", "#F1F5F9");
        SetBrush(resources, "AppItemHoverBorderBrush", "#D7DCE2");
        SetBrush(resources, "AppSelectionBrush", "#E8F1FE");
        SetBrush(resources, "AppSelectionHoverBrush", "#DCEBFD");
        SetBrush(resources, "AppSelectionBorderBrush", "#7AA7E8");
        SetBrush(resources, "AppFilePageBrush", "#FFFFFF");
        SetBrush(resources, "AppFileFoldBrush", "#E5E7EB");
        SetBrush(resources, "AppThumbnailBackgroundBrush", "#FFFFFF");
        SetBrush(resources, "AppBadgeBrush", "#D9111827");
        SetBrush(resources, "AppSelectionOverlayBrush", "#334F83D1");
        SetBrush(resources, "AppAccentBrush", "#4F83D1");
    }

    private static void SetBrush(System.Windows.ResourceDictionary resources, string key, string color)
    {
        resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }
}
