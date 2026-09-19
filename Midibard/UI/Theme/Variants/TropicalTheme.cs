using System.Numerics;

namespace MidiBard;

public class TropicalTheme : UITheme
{
    // rgb(67, 121, 242)  new Vector4(0.2627f, 0.4745f, 0.9490f, 1f);
    // rgb(255, 235, 0)  new Vector4(1.0f, 0.9216f, 0.0f, 1f);
    // rgb(110, 194, 7)  new Vector4(0.4314f, 0.7608f, 0.0275f, 1f);
    // rgb(17, 117, 84)  new Vector4(0.0667f, 0.4588f, 0.3294f, 1f);
    // rgb(33, 146, 255)  new Vector4(0.1294f, 0.5725f, 1.0f, 1f);
    // rgb(56, 229, 77)  new Vector4(0.2196f, 0.8980f, 0.3020f, 1f);
    // rgb(156, 255, 46)  new Vector4(0.6118f, 1.0f, 0.1804f, 1f);
    // rgb(253, 255, 0)  new Vector4(0.9922f, 1.0f, 0.0f, 1f);


    public Vector4 Text { get; init; } = new Vector4(1f, 1f, 1f, 1f); // rgba(255,255,255,1)
    public Vector4 TextDisabled { get; init; } = new Vector4(0.7f, 0.8f, 0.85f, 1f); // rgba(179,204,217,1)
    public Vector4 TextSelectedBg { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)

    public Vector4 WindowBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)
    public Vector4 ChildBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)
    public Vector4 ModalWindowDimBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 0.35f); // rgba(23,33,46,0.35)
    public Vector4 PopupBg { get; init; } = new Vector4(0.18f, 0.36f, 0.29f, 1f); // rgba(46,92,74,1)

    public Vector4 Border { get; init; } = new Vector4(0.29f, 0.56f, 0.47f, 1f); // rgba(74,143,120,1)
    public Vector4 BorderShadow { get; init; } = new Vector4(0f, 0f, 0f, 0f); // rgba(0,0,0,0)

    public Vector4 FrameBg { get; init; } = new Vector4(0.18f, 0.36f, 0.29f, 1f); // rgba(46,92,74,1)
    public Vector4 FrameBgHovered { get; init; } = new Vector4(0.29f, 0.56f, 0.47f, 1f); // rgba(74,143,120,1)
    public Vector4 FrameBgActive { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)

    public Vector4 TitleBg { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 TitleBgActive { get; init; } = new Vector4(0.18f, 0.62f, 0.92f, 1f); // rgba(46,158,235,1)
    public Vector4 TitleBgCollapsed { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)

    public Vector4 MenuBarBg { get; init; } = new Vector4(0.18f, 0.36f, 0.29f, 1f); // rgba(46,92,74,1)
    public Vector4 CheckMark { get; init; } = new Vector4(0.43f, 0.76f, 0.03f, 1f); // rgba(110,194,7,1)
    public Vector4 DragDropTarget { get; init; } = new Vector4(0.98f, 0.58f, 0.22f, 1f); // rgba(250,148,56,1)

    public Vector4 ScrollbarBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)
    public Vector4 ScrollbarGrab { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 ScrollbarGrabHovered { get; init; } = new Vector4(0.18f, 0.62f, 0.92f, 1f); // rgba(46,158,235,1)
    public Vector4 ScrollbarGrabActive { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)

    public Vector4 SliderGrab { get; init; } = new Vector4(0.43f, 0.76f, 0.03f, 1f); // rgba(110,194,7,1)
    public Vector4 SliderGrabActive { get; init; } = new Vector4(0.98f, 0.58f, 0.22f, 1f); // rgba(250,148,56,1)

    public Vector4 Button { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 ButtonHovered { get; init; } = new Vector4(0.18f, 0.62f, 0.92f, 1f); // rgba(46,158,235,1)
    public Vector4 ButtonActive { get; init; } = new Vector4(0.98f, 0.58f, 0.22f, 1f); // rgba(250,148,56,1)

    public Vector4 Header { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 HeaderHovered { get; init; } = new Vector4(0.18f, 0.62f, 0.92f, 1f); // rgba(46,158,235,1)
    public Vector4 HeaderActive { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)

    public Vector4 Separator { get; init; } = new Vector4(0.29f, 0.56f, 0.47f, 1f); // rgba(74,143,120,1)
    public Vector4 SeparatorHovered { get; init; } = new Vector4(0.43f, 0.76f, 0.03f, 1f); // rgba(110,194,7,1)
    public Vector4 SeparatorActive { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)

    public Vector4 ResizeGrip { get; init; } = new Vector4(0.43f, 0.76f, 0.03f, 1f); // rgba(110,194,7,1)
    public Vector4 ResizeGripHovered { get; init; } = new Vector4(0.98f, 0.58f, 0.22f, 1f); // rgba(250,148,56,1)
    public Vector4 ResizeGripActive { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)

    public Vector4 Tab { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 TabHovered { get; init; } = new Vector4(0.18f, 0.62f, 0.92f, 1f); // rgba(46,158,235,1)
    public Vector4 TabActive { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)
    public Vector4 TabUnfocused { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)
    public Vector4 TabUnfocusedActive { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)

    public Vector4 DockingPreview { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 0.1f); // rgba(250,217,94,0.1)
    public Vector4 DockingEmptyBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)

    public Vector4 PlotLines { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 PlotLinesHovered { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)
    public Vector4 PlotHistogram { get; init; } = new Vector4(0.43f, 0.76f, 0.03f, 1f); // rgba(110,194,7,1)
    public Vector4 PlotHistogramHovered { get; init; } = new Vector4(0.98f, 0.58f, 0.22f, 1f); // rgba(250,148,56,1)

    public Vector4 TableHeaderBg { get; init; } = new Vector4(0.18f, 0.62f, 0.92f, 1f); // rgba(46,158,235,1)
    public Vector4 TableBorderStrong { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 TableBorderLight { get; init; } = new Vector4(0.29f, 0.56f, 0.47f, 1f); // rgba(74,143,120,1)
    public Vector4 TableRowBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 1f); // rgba(23,33,46,1)
    public Vector4 TableRowBgAlt { get; init; } = new Vector4(0.18f, 0.36f, 0.29f, 1f); // rgba(46,92,74,1)

    public Vector4 NavHighlight { get; init; } = new Vector4(0.98f, 0.85f, 0.37f, 1f); // rgba(250,217,94,1)
    public Vector4 NavWindowingHighlight { get; init; } = new Vector4(0.13f, 0.47f, 0.78f, 1f); // rgba(33,120,199,1)
    public Vector4 NavWindowingDimBg { get; init; } = new Vector4(0.09f, 0.13f, 0.18f, 0.2f); // rgba(23,33,46,0.2)
}
