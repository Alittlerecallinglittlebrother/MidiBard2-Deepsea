using System.Numerics;

namespace MidiBard;

public class CottonCandyTheme : UITheme
{
    // rgb(168, 216, 234) = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f)
    // rgb(170, 150, 218) = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f)
    // rgb(252, 186, 211) = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f)
    // rgb(255, 255, 210) = new Vector4(1.0f, 1.0f, 0.8235f, 1f)

    public Vector4 Text { get; init; } = new Vector4(0f, 0f, 0f, 1f); // rgb(0, 0, 0)
    public Vector4 TextDisabled { get; init; } = new Vector4(0.5f, 0.5f, 0.55f, 1f); // rgb(128, 128, 140)
    public Vector4 WindowBg { get; init; } = new Vector4(1f, 1f, 0.8235f, 1f); // rgb(255, 255, 210)
    public Vector4 ChildBg { get; init; } = new Vector4(1f, 1f, 0.8235f, 1f); // rgb(255, 255, 210)
    public Vector4 PopupBg { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 0.95f); // rgb(252, 186, 211, 0.95)
    public Vector4 Border { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 BorderShadow { get; init; } = new Vector4(0f, 0f, 0f, 0f); // rgb(0, 0, 0, 0)

    public Vector4 FrameBg { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 FrameBgHovered { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 FrameBgActive { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)

    public Vector4 TitleBg { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 TitleBgActive { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 TitleBgCollapsed { get; init; } = new Vector4(1f, 1f, 0.8235f, 1f); // rgb(255, 255, 210)

    public Vector4 MenuBarBg { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 ScrollbarBg { get; init; } = new Vector4(1f, 1f, 0.8235f, 1f); // rgb(255, 255, 210)
    public Vector4 ScrollbarGrab { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 ScrollbarGrabHovered { get; init; } = new Vector4(0.75f, 0.65f, 0.95f, 1f); // rgb(191, 166, 242)
    public Vector4 ScrollbarGrabActive { get; init; } = new Vector4(0.6f, 0.4f, 0.8f, 1f); // rgb(153, 102, 204)

    public Vector4 CheckMark { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 SliderGrab { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)
    public Vector4 SliderGrabActive { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)

    public Vector4 Button { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 ButtonHovered { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 ButtonActive { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)

    public Vector4 Header { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 HeaderHovered { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 HeaderActive { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)

    public Vector4 Separator { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 SeparatorHovered { get; init; } = new Vector4(0.75f, 0.65f, 0.95f, 1f); // rgb(191, 166, 242)
    public Vector4 SeparatorActive { get; init; } = new Vector4(0.6f, 0.4f, 0.8f, 1f); // rgb(153, 102, 204)

    public Vector4 ResizeGrip { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 ResizeGripHovered { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 ResizeGripActive { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)

    public Vector4 Tab { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 TabHovered { get; init; } = new Vector4(0.75f, 0.65f, 0.95f, 1f); // rgb(191, 166, 242)
    public Vector4 TabActive { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)
    public Vector4 TabUnfocused { get; init; } = new Vector4(0.9f, 0.9f, 0.9f, 1f); // rgb(229, 229, 229)
    public Vector4 TabUnfocusedActive { get; init; } = new Vector4(0.8f, 0.8f, 0.95f, 1f); // rgb(204, 204, 242)

    public Vector4 DockingPreview { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 0.35f); // rgb(252, 186, 211, 0.35)
    public Vector4 DockingEmptyBg { get; init; } = new Vector4(1f, 1f, 0.8235f, 1f); // rgb(255, 255, 210)

    public Vector4 PlotLines { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 PlotLinesHovered { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)
    public Vector4 PlotHistogram { get; init; } = new Vector4(0.75f, 0.65f, 0.95f, 1f); // rgb(191, 166, 242)
    public Vector4 PlotHistogramHovered { get; init; } = new Vector4(0.6f, 0.4f, 0.8f, 1f); // rgb(153, 102, 204)

    public Vector4 TableHeaderBg { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 TableBorderStrong { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)
    public Vector4 TableBorderLight { get; init; } = new Vector4(0.8f, 0.7f, 0.9f, 1f); // rgb(204, 178, 229)
    public Vector4 TableRowBg { get; init; } = new Vector4(0.75f, 0.65f, 0.95f, 1f); // rgb(191, 166, 242)
    public Vector4 TableRowBgAlt { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)

    public Vector4 TextSelectedBg { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 1f); // rgb(252, 186, 211)
    public Vector4 DragDropTarget { get; init; } = new Vector4(0.6667f, 0.5882f, 0.8549f, 1f); // rgb(170, 150, 218)

    public Vector4 NavHighlight { get; init; } = new Vector4(0.75f, 0.65f, 0.95f, 1f); // rgb(191, 166, 242)
    public Vector4 NavWindowingHighlight { get; init; } = new Vector4(0.6588f, 0.8471f, 0.9176f, 1f); // rgb(168, 216, 234)
    public Vector4 NavWindowingDimBg { get; init; } = new Vector4(1f, 1f, 0.8235f, 0.6f); // rgb(255, 255, 210, 0.6)
    public Vector4 ModalWindowDimBg { get; init; } = new Vector4(0.9882f, 0.7294f, 0.8274f, 0.6f); // rgb(252, 186, 211, 0.6)
}
