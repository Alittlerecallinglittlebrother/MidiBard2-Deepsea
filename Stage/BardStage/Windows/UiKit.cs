using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace BardStage.Windows;

public static class UiKit
{
    public static Func<ImFontPtr>? IconFont { get; set; }
    public static Action<string, Vector2, Vector2>? ItemBounds { get; set; }
    public static void RecordItem(string id) => ItemBounds?.Invoke(id, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
    public static float Scale => ImGui.GetFontSize() / 17f;
    public static readonly Vector4 Muted = new(0.64f, 0.66f, 0.69f, 1);
    public static readonly Vector4 Accent = new(0.34f, 0.82f, 0.67f, 1);
    public static readonly Vector4 Warning = new(1, 0.70f, 0.36f, 1);

    public static bool Icon(FontAwesomeIcon icon, string id, string tooltip, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        var scale = Scale;
        var font = IconFont?.Invoke();
        if (font.HasValue) ImGui.PushFont(font.Value);
        var clicked = ImGui.Button($"{(char)icon}##{id}", new Vector2(29 * scale, 0));
        if (font.HasValue) ImGui.PopFont();
        ImGui.EndDisabled();
        RecordItem(id);
        Tip(tooltip);
        return clicked;
    }

    public static void Tip(string text)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(440 * Scale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void MutedText(string text) => ImGui.TextColored(Muted, text);
    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(seconds) ? seconds : 0, 0, 31536000));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }
}
