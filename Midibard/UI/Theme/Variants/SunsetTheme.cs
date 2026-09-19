
using System.Numerics;

namespace MidiBard;

public class SunsetTheme : UITheme
{
    // rgb(255, 251, 218)
    // rgb(255, 236, 158)
    // rgb(255, 187, 112)
    // rgb(237, 148, 85)
    public Vector4 Text { get; init; } = new Vector4(0.2f, 0.15f, 0.1f, 1f);
    public Vector4 TextDisabled { get; init; } = new Vector4(0.5f, 0.4f, 0.3f, 1f);
    public Vector4 WindowBg { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f); // rgb(255, 251, 218)
    public Vector4 ChildBg { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 PopupBg { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f); // rgb(255, 236, 158)
    public Vector4 Border { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f); // rgb(237, 148, 85)
    public Vector4 BorderShadow { get; init; } = new Vector4(0f, 0f, 0f, 0.1f);
    public Vector4 FrameBg { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f); // rgb(255, 187, 112)
    public Vector4 FrameBgHovered { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 FrameBgActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 TitleBg { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 TitleBgActive { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 TitleBgCollapsed { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 MenuBarBg { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 ScrollbarBg { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 ScrollbarGrab { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 ScrollbarGrabHovered { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 ScrollbarGrabActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 CheckMark { get; init; } = new Vector4(0f, 0f, 0f, 1f);
    public Vector4 SliderGrab { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 SliderGrabActive { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 Button { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 ButtonHovered { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 ButtonActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 Header { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 HeaderHovered { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 HeaderActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 Separator { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 SeparatorHovered { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 SeparatorActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 ResizeGrip { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 ResizeGripHovered { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 ResizeGripActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 Tab { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 TabHovered { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 TabActive { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 TabUnfocused { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 TabUnfocusedActive { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 DockingPreview { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 0.4f);
    public Vector4 DockingEmptyBg { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 PlotLines { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 PlotLinesHovered { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 PlotHistogram { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 PlotHistogramHovered { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 1f);
    public Vector4 TableHeaderBg { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 1f);
    public Vector4 TableBorderStrong { get; init; } = new Vector4(0.9f, 0.5f, 0.25f, 1f);
    public Vector4 TableBorderLight { get; init; } = new Vector4(0.8f, 0.4f, 0.2f, 1f);
    public Vector4 TableRowBg { get; init; } = new Vector4(1.0f, 0.984f, 0.855f, 1f);
    public Vector4 TableRowBgAlt { get; init; } = new Vector4(1.0f, 0.956f, 0.788f, 1f);
    public Vector4 TextSelectedBg { get; init; } = new Vector4(1.0f, 0.733f, 0.44f, 0.6f);
    public Vector4 DragDropTarget { get; init; } = new Vector4(0.2f, 0.15f, 0.1f, 1f);
    public Vector4 NavHighlight { get; init; } = new Vector4(0.93f, 0.58f, 0.333f, 1f);
    public Vector4 NavWindowingHighlight { get; init; } = new Vector4(0.8f, 0.45f, 0.2f, 1f);
    public Vector4 NavWindowingDimBg { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 0.6f);
    public Vector4 ModalWindowDimBg { get; init; } = new Vector4(1.0f, 0.925f, 0.62f, 0.6f);
}
