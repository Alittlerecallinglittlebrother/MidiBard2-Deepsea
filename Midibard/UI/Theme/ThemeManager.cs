using System;
using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace MidiBard;

public enum ThemeVariant
{
    Default = 0,
    Dark = 1,
    ModernDark = 2,
    Light = 3,
    OceanFishing = 4,
    DeepBlue = 5,
    Catnip = 6,
    Chocobo = 7,
    Dracula = 8,
    Neon = 9,
    Purple = 10,
    Wine = 11,
    BarbiePink = 12,
    CottonCandy = 13,
    Tropical = 14,
    Sunset = 15,
    Orange = 16,
}

public class ThemeManager
{
    public static UITheme CurrentTheme { get; private set; } = new DefaultTheme();

    public ThemeManager()
    {
        CurrentTheme = new DefaultTheme();
    }

    public ThemeManager(ThemeVariant themeType)
    {
        if (!Enum.IsDefined(typeof(ThemeVariant), themeType))
            themeType = ThemeVariant.Default;

        SetTheme(themeType);
    }

    public static void SetTheme(ThemeVariant themeType)
    {
        CurrentTheme = themeType switch
        {
            ThemeVariant.Default => new DefaultTheme(),
            ThemeVariant.Dark => new DarkTheme(),
            ThemeVariant.Light => new LightTheme(),
            ThemeVariant.OceanFishing => new OceanFishingTheme(),
            ThemeVariant.DeepBlue => new DeepBlueTheme(),
            ThemeVariant.Catnip => new CatnipTheme(),
            ThemeVariant.Chocobo => new ChocoboTheme(),
            ThemeVariant.Dracula => new DraculaTheme(),
            ThemeVariant.Wine => new WineTheme(),
            ThemeVariant.Purple => new PurpleTheme(),
            ThemeVariant.BarbiePink => new BarbiePinkTheme(),
            ThemeVariant.CottonCandy => new CottonCandyTheme(),
            ThemeVariant.Tropical => new TropicalTheme(),
            ThemeVariant.Neon => new NeonTheme(),
            ThemeVariant.ModernDark => new ModernDarkTheme(),
            ThemeVariant.Sunset => new SunsetTheme(),
            ThemeVariant.Orange => new OrangeTheme(),
            _ => new DefaultTheme()
        };
    }

    private int pushCount = 0;

    private void PushColor(ImGuiCol color, Vector4 value)
    {
        ImGui.PushStyleColor(color, value);
        pushCount++;
    }

    public void PopThemeStyles()
    {
        ImGui.PopStyleColor(pushCount);
        pushCount = 0;
    }

    public void PushThemeStyles()
    {
        PushColor(ImGuiCol.Text, CurrentTheme.Text);
        PushColor(ImGuiCol.TextDisabled, CurrentTheme.TextDisabled);
        PushColor(ImGuiCol.TextSelectedBg, CurrentTheme.TextSelectedBg);

        PushColor(ImGuiCol.WindowBg, CurrentTheme.WindowBg);
        PushColor(ImGuiCol.MenuBarBg, CurrentTheme.MenuBarBg);
        PushColor(ImGuiCol.ChildBg, CurrentTheme.ChildBg);
        PushColor(ImGuiCol.PopupBg, CurrentTheme.PopupBg);
        PushColor(ImGuiCol.ModalWindowDimBg, CurrentTheme.ModalWindowDimBg);

        PushColor(ImGuiCol.Border, CurrentTheme.Border);
        PushColor(ImGuiCol.BorderShadow, CurrentTheme.BorderShadow);

        PushColor(ImGuiCol.FrameBg, CurrentTheme.FrameBg);
        PushColor(ImGuiCol.FrameBgHovered, CurrentTheme.FrameBgHovered);
        PushColor(ImGuiCol.FrameBgActive, CurrentTheme.FrameBgActive);

        PushColor(ImGuiCol.TitleBg, CurrentTheme.TitleBg);
        PushColor(ImGuiCol.TitleBgActive, CurrentTheme.TitleBgActive);
        PushColor(ImGuiCol.TitleBgCollapsed, CurrentTheme.TitleBgCollapsed);

        PushColor(ImGuiCol.ScrollbarBg, CurrentTheme.ScrollbarBg);
        PushColor(ImGuiCol.ScrollbarGrab, CurrentTheme.ScrollbarGrab);
        PushColor(ImGuiCol.ScrollbarGrabHovered, CurrentTheme.ScrollbarGrabHovered);
        PushColor(ImGuiCol.ScrollbarGrabActive, CurrentTheme.ScrollbarGrabActive);

        PushColor(ImGuiCol.SliderGrab, CurrentTheme.SliderGrab);
        PushColor(ImGuiCol.SliderGrabActive, CurrentTheme.SliderGrabActive);

        PushColor(ImGuiCol.Separator, CurrentTheme.Separator);
        PushColor(ImGuiCol.SeparatorHovered, CurrentTheme.SeparatorHovered);
        PushColor(ImGuiCol.SeparatorActive, CurrentTheme.SeparatorActive);

        PushColor(ImGuiCol.ResizeGrip, CurrentTheme.ResizeGrip);
        PushColor(ImGuiCol.ResizeGripHovered, CurrentTheme.ResizeGripHovered);
        PushColor(ImGuiCol.ResizeGripActive, CurrentTheme.ResizeGripActive);

        PushColor(ImGuiCol.Tab, CurrentTheme.Tab);
        PushColor(ImGuiCol.TabHovered, CurrentTheme.TabHovered);
        PushColor(ImGuiCol.TabActive, CurrentTheme.TabActive);
        PushColor(ImGuiCol.TabUnfocused, CurrentTheme.TabUnfocused);
        PushColor(ImGuiCol.TabUnfocusedActive, CurrentTheme.TabUnfocusedActive);

        PushColor(ImGuiCol.DockingPreview, CurrentTheme.DockingPreview);
        PushColor(ImGuiCol.DockingEmptyBg, CurrentTheme.DockingEmptyBg);

        PushColor(ImGuiCol.PlotLines, CurrentTheme.PlotLines);
        PushColor(ImGuiCol.PlotLinesHovered, CurrentTheme.PlotLinesHovered);
        PushColor(ImGuiCol.PlotHistogram, CurrentTheme.PlotHistogram);
        PushColor(ImGuiCol.PlotHistogramHovered, CurrentTheme.PlotHistogramHovered);

        PushColor(ImGuiCol.TableHeaderBg, CurrentTheme.TableHeaderBg);
        PushColor(ImGuiCol.TableBorderStrong, CurrentTheme.TableBorderStrong);
        PushColor(ImGuiCol.TableBorderLight, CurrentTheme.TableBorderLight);
        PushColor(ImGuiCol.TableRowBg, CurrentTheme.TableRowBg);
        PushColor(ImGuiCol.TableRowBgAlt, CurrentTheme.TableRowBgAlt);

        PushColor(ImGuiCol.NavHighlight, CurrentTheme.NavHighlight);
        PushColor(ImGuiCol.NavWindowingHighlight, CurrentTheme.NavWindowingHighlight);
        PushColor(ImGuiCol.NavWindowingDimBg, CurrentTheme.NavWindowingDimBg);

        PushColor(ImGuiCol.Header, CurrentTheme.Header);
        PushColor(ImGuiCol.HeaderHovered, CurrentTheme.HeaderHovered);
        PushColor(ImGuiCol.HeaderActive, CurrentTheme.HeaderActive);

        PushColor(ImGuiCol.Button, CurrentTheme.Button);
        PushColor(ImGuiCol.ButtonHovered, CurrentTheme.ButtonHovered);
        PushColor(ImGuiCol.ButtonActive, CurrentTheme.ButtonActive);

        PushColor(ImGuiCol.DragDropTarget, CurrentTheme.DragDropTarget);
        PushColor(ImGuiCol.CheckMark, CurrentTheme.CheckMark);
    }
}

public interface UITheme
{
    Vector4 Text { get; }
    public Vector4 TextDisabled { get; }
    public Vector4 TextSelectedBg { get; }
    public Vector4 WindowBg { get; }
    public Vector4 MenuBarBg { get; }
    public Vector4 ChildBg { get; }
    public Vector4 PopupBg { get; }
    public Vector4 Border { get; }
    public Vector4 BorderShadow { get; }
    public Vector4 FrameBg { get; }
    public Vector4 FrameBgHovered { get; }
    public Vector4 FrameBgActive { get; }
    public Vector4 TitleBg { get; }
    public Vector4 TitleBgActive { get; }
    public Vector4 TitleBgCollapsed { get; }
    public Vector4 ScrollbarBg { get; }
    public Vector4 ScrollbarGrab { get; }
    public Vector4 ScrollbarGrabHovered { get; }
    public Vector4 ScrollbarGrabActive { get; }
    public Vector4 CheckMark { get; }
    public Vector4 SliderGrab { get; }
    public Vector4 SliderGrabActive { get; }
    public Vector4 Button { get; }
    public Vector4 ButtonHovered { get; }
    public Vector4 ButtonActive { get; }
    public Vector4 Header { get; }
    public Vector4 HeaderHovered { get; }
    public Vector4 HeaderActive { get; }
    public Vector4 Separator { get; }
    public Vector4 SeparatorHovered { get; }
    public Vector4 SeparatorActive { get; }
    public Vector4 ResizeGrip { get; }
    public Vector4 ResizeGripHovered { get; }
    public Vector4 ResizeGripActive { get; }
    public Vector4 Tab { get; }
    public Vector4 TabHovered { get; }
    public Vector4 TabActive { get; }
    public Vector4 TabUnfocused { get; }
    public Vector4 TabUnfocusedActive { get; }
    public Vector4 DockingPreview { get; }
    public Vector4 DockingEmptyBg { get; }
    public Vector4 PlotLines { get; }
    public Vector4 PlotLinesHovered { get; }
    public Vector4 PlotHistogram { get; }
    public Vector4 PlotHistogramHovered { get; }
    public Vector4 TableHeaderBg { get; }
    public Vector4 TableBorderStrong { get; }
    public Vector4 TableBorderLight { get; }
    public Vector4 TableRowBg { get; }
    public Vector4 TableRowBgAlt { get; }
    public Vector4 NavHighlight { get; }
    public Vector4 NavWindowingHighlight { get; }
    public Vector4 NavWindowingDimBg { get; }
    public Vector4 DragDropTarget { get; }
    public Vector4 ModalWindowDimBg { get; }

    // public float WindowRounding = 5.3f;
    // public float FrameRounding = 2.3f;
    // public float ScrollbarRounding = 0;
}
