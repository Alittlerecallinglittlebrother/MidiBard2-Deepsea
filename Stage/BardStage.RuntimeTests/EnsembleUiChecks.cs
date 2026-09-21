using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;

internal static unsafe partial class RuntimeUi
{
    private static void RenderEnsembleRegression(string output)
    {
        var window = new MidiBard.PluginUI();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MidiBard.PluginUI).GetField("ShowEnsembleWindow", flags)!.SetValue(window, true);
        var draw = typeof(MidiBard.PluginUI).GetMethod("DrawEnsembleWindow", flags)!;
        foreach (var state in new[] { "empty", "solo", "unconfigured", "loading", "assigned" })
        {
            MidiBard.PluginUI.LogPath = Path.Combine(output, "ensemble-" + state + ".log");
            File.WriteAllText(MidiBard.PluginUI.LogPath, "");
            MidiBard.Managers.PlaylistManager.IsLoading = state == "loading";
            MidiBard.MidiBard.CurrentPlayback = state == "empty" ? null : new MidiBard.PlaybackFixture
            {
                IsSoloPlayback = state == "solo",
                MidiFileConfig = state == "assigned" ? new MidiBard.MidiFileConfig
                {
                    AutomaticallyAssigned = true,
                    Tracks = [new() { Name = "Sax", Enabled = true, AssignedCids = [1] }]
                } : null
            };
            for (var frame = 0; frame < 2; frame++)
            {
                ImGui.GetIO().DisplaySize = new Vector2(1100, 740);
                ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new Vector2(1100, 740));
                draw.Invoke(window, null);
                ImGui.LogFinish();
                ImGui.Render();
                if (ImGui.GetDrawData().TotalVtxCount == 0) throw new InvalidOperationException("blank ensemble view");
            }
            SoftwareRenderer.Save(Path.Combine(output, "ensemble-" + state + ".png"));
            var text = File.ReadAllText(MidiBard.PluginUI.LogPath);
            if (text.Contains("NullReferenceException", StringComparison.Ordinal) || text.Contains("System.", StringComparison.Ordinal))
                throw new InvalidOperationException("ensemble view displayed an exception: " + state);
            var expected = state switch
            {
                "solo" => "当前曲目：单人演奏",
                "unconfigured" => "当前曲目未加载合奏配置",
                "loading" => "正在载入曲目",
                "assigned" => "1 / 1 轨已分配",
                _ => "Select a song"
            };
            if (!text.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException("missing ensemble state: " + state);
            Console.WriteLine("PASS: production ensemble window renders " + state + " without exception text or ImGui stack errors");
        }
        MidiBard.Managers.PlaylistManager.IsLoading = false;
        MidiBard.MidiBard.CurrentPlayback = null;
    }

}

// Only game services and the unrelated native toolbar are replaced. DrawEnsembleWindow
// is linked unchanged from the plugin so the null-state regression exercises production UI.
namespace MidiBard
{
    public partial class PluginUI
    {
        public static string LogPath = "";
        private void DrawEnsembleControlMenu()
        {
            ImGui.LogToFile(-1, LogPath);
            if (PartyChatCommand.EnsembleLoadIssue is { } issue) ImGui.TextWrapped(issue);
        }
        // Native game icon textures are substituted; preserve the production picker's footprint.
        private bool InstrumentPicker(string id, ref uint instrument)
        {
            ImGui.Button($"琴##{id}", new(ImGui.GetFrameHeight(), ImGui.GetFrameHeight()));
            return false;
        }
    }
    internal static class api
    {
        internal static PartyFixture PartyList = new();
        internal static ChatFixture ChatGui = new();
    }
    internal sealed class ChatFixture { internal void PrintError(string message) { } }
    internal static class PartyChatCommand
    {
        internal static bool IsLoading => Managers.PlaylistManager.IsLoading;
        internal static bool ManualDistributionMode => MidiBard.config.playOnMultipleDevices
            && MidiBard.config.EnableCrossComputerSongSync && !MidiBard.config.AutoAssignEnsembleTracks;
        internal static string ManualDistributionStatus => "分配尚未下发";
        internal static string? EnsembleLoadIssue => ManualDistributionMode ? "请完成手动分配并下发，等待全员载入成功后再开始" : null;
        internal static int ManualClicks;
        internal static void SendManualAssignment() { ManualClicks++; }
        internal static void InvalidateAssignment() { }
    }
    internal sealed class PartyFixture : List<MemberFixture>
    {
        internal PartyFixture() { Add(new MemberFixture()); }
        internal bool IsPartyLeader() => true;
    }
    internal sealed class MemberFixture
    {
        internal ulong ContentId => 1;
        internal string Name => "Player";
        internal uint EntityId => 1;
        internal IntPtr Address => IntPtr.Zero;
        internal (ulong Cid, string Name, string World) GetPartyMemberData() => (1, "Player", "World");
    }
    internal static class MidiBard
    {
        internal static ConfigFixture config = new();
        internal static PlaybackFixture? CurrentPlayback;
        internal static bool IsPlaying => false;
        internal static MetronomeFixture AgentMetronome = new();
        internal static void SaveConfig() { }
    }
    internal sealed class MetronomeFixture { internal bool EnsembleModeRunning => false; }
    internal sealed class ConfigFixture
    {
        internal bool AutoAssignEnsembleTracks = true;
        internal bool playOnMultipleDevices = false;
        internal bool usingFileSharingServices = false;
        internal bool EnableCrossComputerSongSync;
        internal List<MemberConfigFixture> EnsembleMemberConfigs = [];
    }
    internal sealed class MemberConfigFixture { internal ulong Cid => 1; }
    internal sealed class PlaybackFixture
    {
        internal bool IsSoloPlayback;
        internal MidiFileConfig? MidiFileConfig;
        internal object[] TrackInfos = [];
        internal string FilePath => "fixture.mid";
        internal void SyncTrackStatusWithMidiFileConfig() { }
    }
    internal sealed class MidiFileConfig
    {
        internal bool AutomaticallyAssigned;
        internal bool LeaderDistributed;
        internal List<TrackFixture> Tracks = [];
        internal static ulong GetFirstCidInParty(TrackFixture track) => track.AssignedCids.FirstOrDefault();
        internal void Save(string path) { }
    }
    internal sealed class TrackFixture
    {
        internal int Index = 0;
        internal string Name = "";
        internal bool Enabled;
        internal uint Instrument = 1;
        internal int Transpose = 0;
        internal List<ulong> AssignedCids = [];
    }
    internal static class ThemeManager
    {
        internal static ThemeFixture CurrentTheme = new();
    }
    internal sealed class ThemeFixture
    {
        internal Vector4 Text => Vector4.One;
        internal Vector4 TextDisabled => new(.5f, .5f, .5f, 1);
    }
    internal static class ImGuiUtil
    {
        internal static bool InputIntWithReset(string id, ref int value, int step, Func<int> reset)
        {
            var changed = ImGui.InputInt(id, ref value, step);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) { value = reset(); return true; }
            return changed;
        }
        internal static void ToolTip(string text) { }
    }
}
namespace MidiBard.Managers
{
    internal static class DistributedEnsembleAssignment
    {
        internal static global::MidiBard.MidiFileConfig CreateDraft(object[] tracks, global::MidiBard.MidiFileConfig? saved) => saved ?? new();
    }
    internal static class PlaylistManager { internal static bool IsLoading; }
    internal static class AutomaticEnsembleAssignment
    {
        internal static global::MidiBard.MidiFileConfig Create(object[] tracks, global::MidiBard.MidiFileConfig? saved) => throw new NotSupportedException();
    }
    internal static class MidiFileConfigManager
    {
        internal static global::MidiBard.MidiFileConfig? GetMidiConfigFromFile(string path) => null;
    }
}
namespace MidiBard.Managers.Ipc { internal sealed class NamespaceMarker { } }
namespace MidiBard.IPC
{
    internal static class IPCHandles
    {
        internal static void UpdateMidiFileConfig(global::MidiBard.MidiFileConfig config) { }
        internal static void SyncAllSettings() { }
    }
}
namespace MidiBard2.Resources
{
    internal static class Language
    {
        internal static string window_title_ensemble_panel => "Ensemble";
        internal static string ensemble_select_a_song_from_playlist => "Select a song";
        internal static string ensemble_combo_tooltip_assign_track_character => "Performer";
    }
}
