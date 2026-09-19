using System.Numerics;

namespace MidiBard;

public class DarkTheme : UITheme
{
    public Vector4 Text { get; init; } = new Vector4(0.95f, 0.95f, 0.95f, 1f); // rgba(242,242,242,1)
    public Vector4 TextDisabled { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 1f); // rgba(128,128,128,1)
    public Vector4 TextSelectedBg { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    // windows background
    public Vector4 WindowBg { get; init; } = new Vector4(0.1f, 0.1f, 0.1f, 1f); // rgba(26,26,26,1)
    public Vector4 ChildBg { get; init; } = new Vector4(0.1f, 0.1f, 0.1f, 1f); // rgba(26,26,26,1)
    public Vector4 ModalWindowDimBg { get; init; } = new Vector4(0f, 0f, 0f, 0.35f); // rgba(0,0,0,0.35)
    public Vector4 PopupBg { get; init; } = new Vector4(0.12f, 0.12f, 0.12f, 1f); // rgba(31,31,31,1)

    public Vector4 Border { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    public Vector4 BorderShadow { get; init; } = new Vector4(0f, 0f, 0f, 0f); // rgba(0,0,0,0)

    // inputs background
    public Vector4 FrameBg { get; init; } = new Vector4(0.25f, 0.25f, 0.25f, 1f); // rgba(64,64,64,1)
    public Vector4 FrameBgHovered { get; init; } = new Vector4(0.35f, 0.35f, 0.35f, 1f); // rgb(90, 90, 90, 1)
    public Vector4 FrameBgActive { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)

    //window title background
    public Vector4 TitleBg { get; init; } = new Vector4(0.15f, 0.15f, 0.15f, 1f); // rgba(38,38,38,1)
    public Vector4 TitleBgActive { get; init; } = new Vector4(0.2f, 0.2f, 0.2f, 1f); // rgba(51,51,51,1)
    public Vector4 TitleBgCollapsed { get; init; } = new Vector4(0.1f, 0.1f, 0.1f, 1f); // rgba(26,26,26,1)

    // ??
    public Vector4 MenuBarBg { get; init; } = new Vector4(0.14f, 0.14f, 0.14f, 1f); // rgba(36,36,36,1)
    public Vector4 CheckMark { get; init; } = new Vector4(0.8f, 0.8f, 0.8f, 1f); // rgba(204,204,204,1)
    public Vector4 DragDropTarget { get; init; } = new Vector4(0.8f, 0.8f, 0.8f, 1f); // rgba(204,204,204,1)

    public Vector4 ScrollbarBg { get; init; } = new Vector4(0.1f, 0.1f, 0.1f, 1f); // rgba(26,26,26,1)
    public Vector4 ScrollbarGrab { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    public Vector4 ScrollbarGrabHovered { get; init; } = new Vector4(0.4f, 0.4f, 0.4f, 1f); // rgba(102,102,102,1)
    public Vector4 ScrollbarGrabActive { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 1f); // rgba(128,128,128,1)

    public Vector4 SliderGrab { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 1f); // rgba(128,128,128,1)
    public Vector4 SliderGrabActive { get; init; } = new Vector4(0.6f, 0.6f, 0.6f, 1f); // rgba(153,153,153,1)

    public Vector4 Button { get; init; } = new Vector4(0.2f, 0.2f, 0.2f, 1f); // rgba(51,51,51,1)
    public Vector4 ButtonHovered { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    public Vector4 ButtonActive { get; init; } = new Vector4(0.4f, 0.4f, 0.4f, 1f); // rgba(102,102,102,1)

    public Vector4 Header { get; init; } = new Vector4(0.25f, 0.25f, 0.25f, 1f); // rgba(64,64,64,1)
    public Vector4 HeaderHovered { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    public Vector4 HeaderActive { get; init; } = new Vector4(0.35f, 0.35f, 0.35f, 1f); // rgba(89,89,89,1)

    public Vector4 Separator { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    public Vector4 SeparatorHovered { get; init; } = new Vector4(0.4f, 0.4f, 0.4f, 1f); // rgba(102,102,102,1)
    public Vector4 SeparatorActive { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 1f); // rgba(128,128,128,1)

    public Vector4 ResizeGrip { get; init; } = new Vector4(0.25f, 0.25f, 0.25f, 1f); // rgba(64,64,64,1)
    public Vector4 ResizeGripHovered { get; init; } = new Vector4(0.35f, 0.35f, 0.35f, 1f); // rgba(89,89,89,1)
    public Vector4 ResizeGripActive { get; init; } = new Vector4(0.45f, 0.45f, 0.45f, 1f); // rgba(115,115,115,1)

    public Vector4 Tab { get; init; } = new Vector4(0.2f, 0.2f, 0.2f, 1f); // rgba(51,51,51,1)
    public Vector4 TabHovered { get; init; } = new Vector4(0.3f, 0.3f, 0.3f, 1f); // rgba(77,77,77,1)
    public Vector4 TabActive { get; init; } = new Vector4(0.35f, 0.35f, 0.35f, 1f); // rgba(89,89,89,1)
    public Vector4 TabUnfocused { get; init; } = new Vector4(0.15f, 0.15f, 0.15f, 1f); // rgba(38,38,38,1)
    public Vector4 TabUnfocusedActive { get; init; } = new Vector4(0.2f, 0.2f, 0.2f, 1f); // rgba(51,51,51,1)

    public Vector4 DockingPreview { get; init; } = new Vector4(1f, 1f, 1f, 0.1f); // rgba(255,255,255,0.1)
    public Vector4 DockingEmptyBg { get; init; } = new Vector4(0.1f, 0.1f, 0.1f, 1f); // rgba(26,26,26,1)

    public Vector4 PlotLines { get; init; } = new Vector4(0.6f, 0.6f, 0.6f, 1f); // rgba(153,153,153,1)
    public Vector4 PlotLinesHovered { get; init; } = new Vector4(0.8f, 0.8f, 0.8f, 1f); // rgba(204,204,204,1)
    public Vector4 PlotHistogram { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 1f); // rgba(128,128,128,1)
    public Vector4 PlotHistogramHovered { get; init; } = new Vector4(0.7f, 0.7f, 0.7f, 1f); // rgba(179,179,179,1)

    public Vector4 TableHeaderBg { get; init; } = new Vector4(0.2f, 0.2f, 0.2f, 1f); // rgba(51,51,51,1)
    public Vector4 TableBorderStrong { get; init; } = new Vector4(0.4f, 0.4f, 0.4f, 1f); // rgba(102,102,102,1)
    public Vector4 TableBorderLight { get; init; } = new Vector4(0.25f, 0.25f, 0.25f, 1f); // rgba(64,64,64,1)
    public Vector4 TableRowBg { get; init; } = new Vector4(0.15f, 0.15f, 0.15f, 1f); // rgba(38,38,38,1)
    public Vector4 TableRowBgAlt { get; init; } = new Vector4(0.13f, 0.13f, 0.13f, 1f); // rgba(33,33,33,1)

    public Vector4 NavHighlight { get; init; } = new Vector4(0.35f, 0.35f, 0.35f, 1f); // rgba(89,89,89,1)
    public Vector4 NavWindowingHighlight { get; init; } = new Vector4(0.8f, 0.8f, 0.8f, 0.7f); // rgba(204,204,204,0.7)
    public Vector4 NavWindowingDimBg { get; init; } = new Vector4(0f, 0f, 0f, 0.2f); // rgba(0,0,0,0.2)
}
