using System.Numerics;

namespace MidiBard;

public class DefaultTheme : UITheme
{
    public Vector4 Text { get; init; } = new Vector4(1f, 1f, 1f, 1f);                         // #FFFFFF
    public Vector4 TextDisabled { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 1f);           // #808080
    public Vector4 TextSelectedBg { get; init; } = new Vector4(0.26f, 0.59f, 0.98f, 0.35f);   // #4195F2
    public Vector4 WindowBg { get; init; } = new Vector4(0.06f, 0.06f, 0.06f, 0.93f);         // #0F0F0FEC
    public Vector4 MenuBarBg { get; init; } = new Vector4(0.14f, 0.14f, 0.14f, 1f);           // #232323
    public Vector4 ChildBg { get; init; } = new Vector4(0f, 0f, 0f, 0f);                     // #00000000
    public Vector4 PopupBg { get; init; } = new Vector4(0.08f, 0.08f, 0.08f, 0.94f);          // #141414F0
    public Vector4 Border { get; init; } = new Vector4(0.43f, 0.43f, 0.5f, 0.5f);             // #6E6E80
    public Vector4 BorderShadow { get; init; } = new Vector4(0f, 0f, 0f, 0f);                 // #00000000
    public Vector4 FrameBg { get; init; } = new Vector4(0.29f, 0.29f, 0.29f, 0.54f);          // #4A4A4A8A
    public Vector4 FrameBgHovered { get; init; } = new Vector4(0.54f, 0.54f, 0.54f, 0.4f);     // #87878766
    public Vector4 FrameBgActive { get; init; } = new Vector4(0.64f, 0.64f, 0.64f, 0.67f);     // #A3A3A8AB
    public Vector4 TitleBg { get; init; } = new Vector4(0.022624433f, 0.022624206f, 0.022624206f, 0.85067874f);  // #060606D8
    public Vector4 TitleBgActive { get; init; } = new Vector4(0.38914025f, 0.10917056f, 0.10917056f, 0.8280543f);  // #639F1BDB
    public Vector4 TitleBgCollapsed { get; init; } = new Vector4(0f, 0f, 0f, 0.51f);           // #00000082
    public Vector4 ScrollbarBg { get; init; } = new Vector4(0f, 0f, 0f, 0f);                  // #00000000
    public Vector4 ScrollbarGrab { get; init; } = new Vector4(0.31f, 0.31f, 0.31f, 1f);        // #4F4F4F
    public Vector4 ScrollbarGrabHovered { get; init; } = new Vector4(0.41f, 0.41f, 0.41f, 1f);  // #696969
    public Vector4 ScrollbarGrabActive { get; init; } = new Vector4(0.51f, 0.51f, 0.51f, 1f);  // #828282
    public Vector4 CheckMark { get; init; } = new Vector4(0.86f, 0.86f, 0.86f, 1f);            // #DBDBDB
    public Vector4 SliderGrab { get; init; } = new Vector4(0.54f, 0.54f, 0.54f, 1f);           // #8A8A8A
    public Vector4 SliderGrabActive { get; init; } = new Vector4(0.67f, 0.67f, 0.67f, 1f);     // #A8A8A8
    public Vector4 Button { get; init; } = new Vector4(0.71f, 0.71f, 0.71f, 0.4f);            // #B4B4B4
    public Vector4 ButtonHovered { get; init; } = new Vector4(0.3647059f, 0.078431375f, 0.078431375f, 0.94509804f);  // #5D1414F2
    public Vector4 ButtonActive { get; init; } = new Vector4(0.48416287f, 0.10077597f, 0.10077597f, 0.94509804f); // #7C1A1AF2
    public Vector4 Header { get; init; } = new Vector4(0.59f, 0.59f, 0.59f, 0.31f);            // #9494944F
    public Vector4 HeaderHovered { get; init; } = new Vector4(0.5f, 0.5f, 0.5f, 0.8f);          // #808080CC
    public Vector4 HeaderActive { get; init; } = new Vector4(0.6f, 0.6f, 0.6f, 1f);            // #999999
    public Vector4 Separator { get; init; } = new Vector4(0.43f, 0.43f, 0.5f, 0.5f);           // #6E6E80
    public Vector4 SeparatorHovered { get; init; } = new Vector4(0.3647059f, 0.078431375f, 0.078431375f, 0.78280544f);  // #5D1414C7
    public Vector4 SeparatorActive { get; init; } = new Vector4(0.3647059f, 0.078431375f, 0.078431375f, 0.94509804f);  // #5D1414F2
    public Vector4 ResizeGrip { get; init; } = new Vector4(0.79f, 0.79f, 0.79f, 0.25f);         // #CACACA40
    public Vector4 ResizeGripHovered { get; init; } = new Vector4(0.78f, 0.78f, 0.78f, 0.67f);   // #C8C8C8AB
    public Vector4 ResizeGripActive { get; init; } = new Vector4(0.3647059f, 0.078431375f, 0.078431375f, 0.94509804f);  // #5D1414F2
    public Vector4 Tab { get; init; } = new Vector4(0.23f, 0.23f, 0.23f, 0.86f);               // #3A3A3AF7
    public Vector4 TabHovered { get; init; } = new Vector4(0.58371043f, 0.30374074f, 0.30374074f, 0.7647059f);  // #94504C
    public Vector4 TabActive { get; init; } = new Vector4(0.47963798f, 0.15843244f, 0.15843244f, 0.7647059f);  // #7A2828
    public Vector4 TabUnfocused { get; init; } = new Vector4(0.068f, 0.10199998f, 0.14800003f, 0.9724f);       // #112648
    public Vector4 TabUnfocusedActive { get; init; } = new Vector4(0.13599998f, 0.26199996f, 0.424f, 1f);         // #227A6B
    public Vector4 DockingPreview { get; init; } = new Vector4(0.26f, 0.59f, 0.98f, 0.7f);                      // #4195F2B3
    public Vector4 DockingEmptyBg { get; init; } = new Vector4(0.2f, 0.2f, 0.2f, 1f);                          // #333333
    public Vector4 PlotLines { get; init; } = new Vector4(0.61f, 0.61f, 0.61f, 1f);                            // #9B9B9B
    public Vector4 PlotLinesHovered { get; init; } = new Vector4(1f, 0.43f, 0.35f, 1f);                        // #FF6F59
    public Vector4 PlotHistogram { get; init; } = new Vector4(0.9f, 0.7f, 0f, 1f);                             // #E5B200
    public Vector4 PlotHistogramHovered { get; init; } = new Vector4(1f, 0.6f, 0f, 1f);                         // #FF9900
    public Vector4 TableHeaderBg { get; init; } = new Vector4(0.19f, 0.19f, 0.2f, 1f);                         // #303030
    public Vector4 TableBorderStrong { get; init; } = new Vector4(0.31f, 0.31f, 0.35f, 1f);                     // #4F4F59
    public Vector4 TableBorderLight { get; init; } = new Vector4(0.23f, 0.23f, 0.25f, 1f);                      // #3A3A40
    public Vector4 TableRowBg { get; init; } = new Vector4(0f, 0f, 0f, 0f);                                    // #00000000
    public Vector4 TableRowBgAlt { get; init; } = new Vector4(1f, 1f, 1f, 0.06f);                              // #FFFFFF0F
    public Vector4 NavHighlight { get; init; } = new Vector4(0.26f, 0.59f, 0.98f, 1f);                         // #4195F2
    public Vector4 NavWindowingHighlight { get; init; } = new Vector4(1f, 1f, 1f, 0.7f);                        // #FFFFFFB3
    public Vector4 NavWindowingDimBg { get; init; } = new Vector4(0.8f, 0.8f, 0.8f, 0.2f);                      // #CCCCCC33
    public Vector4 DragDropTarget { get; init; } = new Vector4(1f, 1f, 0f, 0.9f);                              // #FFFF00E6
    public Vector4 ModalWindowDimBg { get; init; } = new Vector4(0.8f, 0.8f, 0.8f, 0.35f);                      // #CCCCCC59
}
