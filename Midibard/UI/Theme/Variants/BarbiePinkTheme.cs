using System.Numerics;

namespace MidiBard;

public class BarbiePinkTheme : UITheme
{
    private static readonly Vector4 ColorPalette1 = new Vector4(0.15f, 0.05f, 0.15f, 1f); // rgba(38,13,38,1)
    private static readonly Vector4 ColorPalette2 = new Vector4(0.2f, 0.1f, 0.2f, 1f);    // rgba(51,26,51,1)
    private static readonly Vector4 ColorPalette3 = new Vector4(0.6f, 0.3f, 0.6f, 1f);    // rgba(153,77,153,1)
    private static readonly Vector4 ColorPalette4 = new Vector4(0.8f, 0.5f, 0.8f, 1f);    // rgba(204,128,204,1)
    private static readonly Vector4 ColorPalette5 = new Vector4(0.9f, 0.5f, 0.9f, 1f);    // rgba(230,128,230,1)
    private static readonly Vector4 ColorPalette6 = new Vector4(1.0f, 0.7f, 1.0f, 1f);    // rgba(255,179,255,1)
    private static readonly Vector4 ColorPalette7 = new Vector4(0.9f, 0.6f, 0.9f, 1f);    // rgba(230,153,230,1)

    public Vector4 Text { get; init; } = new Vector4(1.0f, 0.85f, 0.95f, 1f); // rgba(255,217,242,1)
    public Vector4 TextDisabled { get; init; } = new Vector4(0.8f, 0.7f, 0.75f, 1f); // rgba(204,179,191,1)
    public Vector4 WindowBg { get; init; } = ColorPalette1;
    public Vector4 ChildBg { get; init; } = ColorPalette1;
    public Vector4 PopupBg { get; init; } = ColorPalette2;
    public Vector4 Border { get; init; } = ColorPalette3;
    public Vector4 BorderShadow { get; init; } = new Vector4(0f, 0f, 0f, 0f); // rgba(0,0,0,0)
    public Vector4 FrameBg { get; init; } = ColorPalette4;
    public Vector4 FrameBgHovered { get; init; } = ColorPalette7;
    public Vector4 FrameBgActive { get; init; } = ColorPalette6;
    public Vector4 TitleBg { get; init; } = ColorPalette2;
    public Vector4 TitleBgActive { get; init; } = new Vector4(0.4f, 0.2f, 0.4f, 1f); // rgba(102,51,102,1)
    public Vector4 TitleBgCollapsed { get; init; } = ColorPalette1;
    public Vector4 MenuBarBg { get; init; } = ColorPalette2;
    public Vector4 ScrollbarBg { get; init; } = new Vector4(0.1f, 0.05f, 0.1f, 1f); // rgba(26,13,26,1)
    public Vector4 ScrollbarGrab { get; init; } = ColorPalette3;
    public Vector4 ScrollbarGrabHovered { get; init; } = ColorPalette4;
    public Vector4 ScrollbarGrabActive { get; init; } = ColorPalette7;
    public Vector4 CheckMark { get; init; } = ColorPalette6;
    public Vector4 SliderGrab { get; init; } = ColorPalette4;
    public Vector4 SliderGrabActive { get; init; } = ColorPalette5;
    public Vector4 Button { get; init; } = ColorPalette3;
    public Vector4 ButtonHovered { get; init; } = ColorPalette4;
    public Vector4 ButtonActive { get; init; } = ColorPalette5;
    public Vector4 Header { get; init; } = ColorPalette4;
    public Vector4 HeaderHovered { get; init; } = ColorPalette5;
    public Vector4 HeaderActive { get; init; } = ColorPalette6;
    public Vector4 Separator { get; init; } = ColorPalette3;
    public Vector4 SeparatorHovered { get; init; } = ColorPalette4;
    public Vector4 SeparatorActive { get; init; } = ColorPalette5;
    public Vector4 ResizeGrip { get; init; } = ColorPalette4;
    public Vector4 ResizeGripHovered { get; init; } = ColorPalette5;
    public Vector4 ResizeGripActive { get; init; } = ColorPalette6;
    public Vector4 Tab { get; init; } = ColorPalette3;
    public Vector4 TabHovered { get; init; } = ColorPalette4;
    public Vector4 TabActive { get; init; } = ColorPalette5;
    public Vector4 TabUnfocused { get; init; } = ColorPalette1;
    public Vector4 TabUnfocusedActive { get; init; } = ColorPalette2;
    public Vector4 DockingPreview { get; init; } = new Vector4(1.0f, 0.6f, 1.0f, 0.4f); // rgba(255,153,255,0.4)
    public Vector4 DockingEmptyBg { get; init; } = new Vector4(0.1f, 0.05f, 0.1f, 1f); // rgba(26,13,26,1)
    public Vector4 PlotLines { get; init; } = ColorPalette6;
    public Vector4 PlotLinesHovered { get; init; } = ColorPalette6;
    public Vector4 PlotHistogram { get; init; } = ColorPalette4;
    public Vector4 PlotHistogramHovered { get; init; } = ColorPalette5;
    public Vector4 TableHeaderBg { get; init; } = ColorPalette3;
    public Vector4 TableBorderStrong { get; init; } = ColorPalette3;
    public Vector4 TableBorderLight { get; init; } = new Vector4(0.5f, 0.2f, 0.5f, 1f); // rgba(128,51,128,1)
    public Vector4 TableRowBg { get; init; } = ColorPalette1;
    public Vector4 TableRowBgAlt { get; init; } = ColorPalette2;
    public Vector4 TextSelectedBg { get; init; } = ColorPalette5;
    public Vector4 DragDropTarget { get; init; } = ColorPalette6;
    public Vector4 NavHighlight { get; init; } = ColorPalette5;
    public Vector4 NavWindowingHighlight { get; init; } = ColorPalette6;
    public Vector4 NavWindowingDimBg { get; init; } = new Vector4(0.15f, 0.05f, 0.15f, 0.7f); // rgba(38,13,38,0.7)
    public Vector4 ModalWindowDimBg { get; init; } = new Vector4(0.15f, 0.05f, 0.15f, 0.7f); // rgba(38,13,38,0.7)
}
