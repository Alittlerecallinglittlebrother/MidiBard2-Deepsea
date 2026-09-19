using Dalamud.Bindings.ImGui;

using MidiBard.Control.CharacterControl;

using MidiBard2.Resources;

namespace MidiBard;

public partial class PluginUI
{
    private static void InstrumentPickerSolo()
    {
        UIcurrentInstrument = MidiBard.CurrentInstrument;
        if (MidiBard.PlayingGuitar)
        {
            UIcurrentInstrument = (uint)(MidiBard.AgentPerformance.CurrentGroupTone + MidiBard.guitarGroup[0]);
        }

        if (InstrumentPicker($"##instrumentPicker", ref UIcurrentInstrument))
        {
            SwitchInstrument.SwitchToContinue(UIcurrentInstrument);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
        ImGuiUtil.ToolTip(Language.setting_tooltip_select_instrument);
    }
}
