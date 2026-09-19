using System.Numerics;
using System.Reflection;
using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;
using Melanchall.DryWetMidi.Core;

if (args.Length == 2 && args[0] == "--library-check")
{
    var work = Path.Combine(Path.GetTempPath(), "BardStage-library-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    var midi = Path.Combine(work, "library.mid");
    new MidiFile(new TrackChunk(new NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)80), new NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 1920 })).Write(midi);
    LibraryRuntimeChecks.Run(work, midi);
    RuntimeUi.RunLibraryChecks(midi, Path.GetFullPath(args[1]));
    return;
}

if (args.Length == 2 && args[0] == "--room-presenter-check")
{ RoomRuntimeChecks.RunPresenterProcess(args[1]); return; }
if (args.Length == 2 && args[0] == "--room-viewer-check")
{ ViewerRuntimeChecks.RunViewerProcess(args[1]); return; }
if (args.Length == 2 && args[0] == "--handoff-check")
{
    var work = Path.Combine(Path.GetTempPath(), "BardStage-handoff-" + Guid.NewGuid()); Directory.CreateDirectory(work);
    var midi = Path.Combine(work, "test.mid");
    new MidiFile(new TrackChunk(new NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)80),
        new NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 1920 })).Write(midi);
    HandoffRuntimeChecks.Run(work, midi); HandoffRuntimeChecks.RunUi(work, midi, Path.GetFullPath(args[1])); return;
}

if (args.Length == 2 && args[0] == "--validate-upgrade")
{
    var directory = Path.Combine(Path.GetTempPath(), "BardStage-upgrade-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    File.Copy(args[1], Path.Combine(directory, "catalog.json"));
    using var upgrade = new StageController(directory);
    var songs = upgrade.State.Songs.Select(s => s.Id).ToArray();
    var requests = upgrade.State.Requests.Select(r => r.Id).ToArray();
    var sessions = upgrade.State.Sessions.Select(s => s.Id).ToArray();
    Check(!upgrade.IsReadOnly && upgrade.EnsureAutomaticQueue(), "v0.4 catalog copy enables the automatic queue");
    Check(songs.SequenceEqual(upgrade.State.Songs.Select(s => s.Id)) && requests.SequenceEqual(upgrade.State.Requests.Select(r => r.Id))
        && sessions.SequenceEqual(upgrade.State.Sessions.Select(s => s.Id)), "upgrade preserves song, audience request and session identities");
    Console.WriteLine($"UPGRADE: songs={songs.Length}; requests={requests.Length}; sessions={sessions.Length}; live data untouched");
    return;
}

var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "screenshots"));
Directory.CreateDirectory(output);
var scratch = Path.Combine(Path.GetTempPath(), "BardStage-runtime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
using var controller = new StageController(Path.Combine(scratch, "data"));
var midiPath = Path.Combine(scratch, "开场序曲.mid");
new MidiFile(new TrackChunk(new SetTempoEvent(500000), new NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)80), new NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 1920 })).Write(midiPath);
controller.Import([midiPath]);
while (controller.IsBusy) { Thread.Sleep(10); controller.Poll(); }
Check(controller.State.Songs.Count == 1 && !controller.StatusIsError, "async import and persist");
var initial = controller.State.Songs[0];
controller.Change(state =>
{
    var song = state.Songs[0]; song.Arranger = "排练版 v1"; song.PerformerCount = 8;
    song.Aliases = ["Opening", "开场"];
    song.Notes = "主旋律由长笛担任；准备拍后进入。";
    song.DurationSeconds = 260;
    for (var i = 0; i < 10; i++)
    {
        var copy = StageController.Clone(song); copy.Id = Guid.NewGuid(); copy.Title = new[] { "月下华尔兹", "归途", "夏日回声", "星光小夜曲", "海岸线", "远方来信", "晨曦", "最后一支舞", "森林间奏", "谢幕" }[i];
        copy.Arranger = "乐队编曲 " + (i + 1); copy.PerformerCount = i % 3 == 0 ? 0 : 6 + i % 3;
        copy.DurationSeconds = 180 + i * 17; copy.Sha256 = i.ToString("X64"); state.Songs.Add(copy);
    }
    var show = state.Setlists[0]; show.Name = "周五酒馆 · 晚场";
    SetlistOperations.AddSong(show, state.Songs[0]); SetlistOperations.AddSong(show, state.Songs[1]);
    SetlistOperations.AddSegment(show, EntryKind.Talk, "主持串场 · 嘉宾介绍", 60);
    SetlistOperations.AddSong(show, state.Songs[2]); SetlistOperations.AddSegment(show, EntryKind.Break, "中场休息", 600);
    SetlistOperations.AddSong(show, state.Songs[3]); SetlistOperations.AddSong(show, state.Songs[10]);
    show.LockedNextEntryId = show.Entries[1].Id;
});
Check(!controller.StatusIsError, "catalog metadata and typed setlist persisted");
var titleBefore = controller.State.Songs[0].Title;
controller.Change(state => state.Songs[0].Title = "");
Check(controller.StatusIsError && controller.State.Songs[0].Title == titleBefore, "invalid metadata rolls back");
using (var reopened = new StageController(Path.Combine(scratch, "data")))
{
    Check(reopened.State.Songs[0].Title == titleBefore && reopened.CurrentSetlist!.Entries.Count == 7, "second instance reads saved state");
    Check(reopened.IsReadOnly, "second instance cannot overwrite active writer");
}
var exported = Path.Combine(scratch, "show.json");
controller.ExportCurrentSetlist(exported);
Check(File.Exists(exported), "export writes show");
controller.ImportSetlist(exported);
Check(controller.State.Setlists.Count == 2 && !controller.StatusIsError && controller.State.Songs.Count == 11, "import remaps entries and deduplicates songs");
var protectedBefore = File.ReadAllBytes(Path.Combine(scratch, "data", "catalog.json.bak"));
controller.ExportCurrentSetlist(Path.Combine(scratch, "data", "catalog.json.bak"));
Check(controller.StatusIsError && File.ReadAllBytes(Path.Combine(scratch, "data", "catalog.json.bak")).SequenceEqual(protectedBefore), "export cannot overwrite recovery backup");
var invalidShow = Path.Combine(scratch, "invalid.json");
File.WriteAllText(invalidShow, "{}");
controller.ImportSetlist(invalidShow);
Check(controller.StatusIsError && controller.State.Setlists.Count == 2, "invalid import preserves catalog");
controller.Change(state => state.SelectedSetlistId = state.Setlists[0].Id);
var current = controller.CurrentSetlist!;
controller.Change(state => SetlistOperations.Start(state.Setlists[0], current.Entries[0].Id, DateTimeOffset.UtcNow));
controller.Change(state => SetlistOperations.Start(state.Setlists[1], state.Setlists[1].Entries[0].Id, DateTimeOffset.UtcNow));
Check(controller.StatusIsError && controller.State.Setlists.SelectMany(s => s.Entries).Count(e => e.Status == EntryStatus.InProgress) == 1, "cross-show active entry protection");
controller.SetStatus("已保存至本地");
RequestRuntimeChecks.Run(scratch, midiPath);
StageRuntimeChecks.Run(scratch, midiPath);
PlaybackRuntimeChecks.Run(scratch, midiPath);
AutoQueueRuntimeChecks.Run(scratch, midiPath);
RoomRuntimeChecks.Run(scratch, midiPath);
ViewerRuntimeChecks.Run(scratch, midiPath);
CleanupRuntimeChecks.Run(scratch, midiPath);
LibraryRuntimeChecks.Run(scratch, midiPath);
AuthorityRuntimeChecks.Run(scratch, midiPath);
HandoffRuntimeChecks.Run(scratch, midiPath);
RuntimeUi.Run(controller, output);
RuntimeUi.RunAuthorityChecks(midiPath, output);
HandoffRuntimeChecks.RunUi(scratch, midiPath, output);
Console.WriteLine("PASS: native ImGui runtime and controller checks completed. No game playback invoked.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine("PASS: " + message);
}

internal static unsafe partial class RuntimeUi
{
    public static void RunLegacy(StageController controller, string output)
    {
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var iconsPath = Path.Combine(output, "..", "fa-solid-900.ttf");
            if (File.Exists(iconsPath))
            {
                ushort* range = (ushort*)System.Runtime.InteropServices.NativeMemory.Alloc(3, sizeof(ushort));
                range[0] = 0xE000; range[1] = 0xF8FF; range[2] = 0;
                var iconFont = io.Fonts.AddFontFromFileTTF(iconsPath, 17, default, range);
                UiKit.IconFont = () => iconFont;
                if (!io.Fonts.Build()) throw new InvalidOperationException("font atlas failed");
                System.Runtime.InteropServices.NativeMemory.Free(range);
            }
            else if (!io.Fonts.Build()) throw new InvalidOperationException("font atlas failed");
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle(); style.WindowRounding = 0; style.FrameRounding = 3; style.ScrollbarRounding = 3;
            using var window = new MainWindow(controller);
            Invoke(window, "SelectSong", controller.State.Songs[0]);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "library.png"));
            Set(window, "selectSetlistTab", true);
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            Click(window, 100, 245);
            if (Get<Guid?>(window, "selectedEntryId") != controller.CurrentSetlist!.Entries[0].Id) throw new InvalidOperationException("native row selection failed");
            Console.WriteLine("PASS: native mouse selects setlist entry");
            ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "setlist.png"));
            Frame(window, 760, 540); Frame(window, 760, 540);
            SoftwareRenderer.Save(Path.Combine(output, "setlist-small.png"));
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            var activeId = controller.CurrentSetlist.Entries.Single(e => e.Status == EntryStatus.InProgress).Id;
            var lockedId = controller.CurrentSetlist.LockedNextEntryId;
            Click(window, 639, 272);
            if (controller.CurrentSetlist.Entries[0].Id != lockedId || controller.CurrentSetlist.Entries.Single(e => e.Status == EntryStatus.InProgress).Id != activeId)
                throw new InvalidOperationException("native reorder broke identity");
            Console.WriteLine("PASS: native reorder preserves active and locked-next identities");
            Drag(window, new Vector2(100, 245), new Vector2(100, 299));
            if (controller.CurrentSetlist.Entries[2].Id != lockedId || controller.CurrentSetlist.LockedNextEntryId != lockedId)
                throw new InvalidOperationException("native drag/drop failed");
            Console.WriteLine("PASS: native drag/drop reorders by stable ID");
            Click(window, 271, 101);
            Frame(window, 1100, 740);
            if (ImGui.GetDrawData().CmdListsCount < 2) throw new InvalidOperationException("new-show modal failed");
            SoftwareRenderer.Save(Path.Combine(output, "new-show.png"));
            Set(window, "createName", "验收节目单");
            Click(window, 389, 394);
            if (controller.CurrentSetlist?.Name != "验收节目单") throw new InvalidOperationException("native new-show save failed");
            Console.WriteLine("PASS: native modal saves a new setlist");
            controller.Change(state => state.SelectedSetlistId = state.Setlists[0].Id);
            Frame(window, 1100, 740);
            var confirmed = false;
            Invoke(window, "Confirm", "移除这一项？MIDI 原文件会保留。", (Action)(() => confirmed = true));
            Frame(window, 1100, 740); Frame(window, 1100, 740);
            SoftwareRenderer.Save(Path.Combine(output, "confirm.png"));
            Click(window, 383, 394);
            if (!confirmed) throw new InvalidOperationException("native confirmation button failed");
            Console.WriteLine("PASS: native confirmation executes only after clicking confirm");
            RenderRequests(controller, window, output);
            RenderStage(controller, window, output);
            try { RenderSessions(controller, window, output); }
            catch { SoftwareRenderer.Save(Path.Combine(output, "sessions-interaction-last.png")); throw; }
            Console.WriteLine($"PASS: native ImGui rendered at 1100x740 and 760x540; {ImGui.GetDrawData().TotalVtxCount} vertices");
        }
        finally { UiKit.IconFont = null; ImGui.DestroyContext(context); }
    }

    private static void RenderRequests(StageController controller, MainWindow window, string output)
    {
        controller.Change(state =>
        {
            var show = state.Setlists[0];
            state.SelectedSetlistId = show.Id;
            state.RequestSettings.TargetSetlistId = show.Id;
            state.RequestSettings.IsOpen = true;
            var alternate = StageController.Clone(state.Songs[1]);
            alternate.Id = Guid.NewGuid(); alternate.Sha256 = 1000.ToString("X64");
            alternate.Arranger = "六人弦乐版 v2"; alternate.PerformerCount = 6; state.Songs.Add(alternate);
            RequestOperations.Submit(state, show.Id, "观众甲", "海幻沙", "月下华尔兹", RequestChannel.Say, DateTimeOffset.UtcNow);
            RequestOperations.Submit(state, show.Id, "观众乙", "红玉海", "月下华尔兹", RequestChannel.Tell, DateTimeOffset.UtcNow);
            RequestOperations.Submit(state, show.Id, "观众丙", "", "开场序曲", RequestChannel.Manual, DateTimeOffset.UtcNow);
            RequestOperations.Submit(state, show.Id, "观众丁", "海幻沙", "远方的歌", RequestChannel.Say, DateTimeOffset.UtcNow);
        });
        Set(window, "selectRequestTab", true);
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        Invoke(window, "FocusRequest", controller.State.Requests[0], true);
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "requests.png"));
        Frame(window, 760, 540); Frame(window, 760, 540);
        SoftwareRenderer.Save(Path.Combine(output, "requests-small.png"));
        Frame(window, 1100, 740); Frame(window, 1100, 740);
        Click(window, 298, 101); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "request-settings.png"));
        Get<RequestSettings>(window, "settingsDraft").MaxOutstandingPerPerson = 3;
        Get<RequestSettings>(window, "settingsDraft").Prefix = " 点歌 ";
        Click(window, 381, 526);
        if (controller.State.RequestSettings.MaxOutstandingPerPerson != 3 || controller.State.RequestSettings.Prefix != "点歌") throw new InvalidOperationException("native request settings save failed");
        Console.WriteLine("PASS: native request settings modal saves reception policy");
        Click(window, 20, 128);
        if (controller.State.RequestSettings.IsOpen) throw new InvalidOperationException("native request pause failed");
        Click(window, 20, 128);
        if (!controller.State.RequestSettings.IsOpen) throw new InvalidOperationException("native request resume failed");
        Console.WriteLine("PASS: native reception checkbox pauses and resumes chat requests");
        Click(window, 20, 237);
        if (Get<HashSet<Guid>>(window, "checkedRequests").Count != 2) throw new InvalidOperationException("native request multi-selection failed");
        Click(window, 660, 436);
        if (Get<Guid?>(window, "requestSongId") != controller.State.Songs.Last().Id) throw new InvalidOperationException("native request version selection failed");
        Console.WriteLine("PASS: native multi-selection requires an explicit arrangement version");
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "requests-ready.png"));
        var countBefore = controller.CurrentSetlist!.Entries.Count;
        var lockBefore = controller.CurrentSetlist.LockedNextEntryId;
        Click(window, 830, 588);
        var firstTwo = controller.State.Requests.Take(2).ToArray();
        if (controller.CurrentSetlist.Entries.Count != countBefore + 1 || firstTwo.Any(r => r.Status != RequestStatus.Arranged)
            || firstTwo[0].SetlistEntryId != firstTwo[1].SetlistEntryId || controller.CurrentSetlist.LockedNextEntryId != lockBefore)
            throw new InvalidOperationException("native request approval or stable next lock failed");
        Console.WriteLine("PASS: native approval merges two requests into one program and preserves next lock");
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "requests-arranged.png"));
        Click(window, 100, 212);
        Click(window, 654, 627);
        if (controller.State.Requests[2].Status != RequestStatus.Deferred) throw new InvalidOperationException("native request defer failed");
        Click(window, 100, 212);
        Set(window, "rejectReason", "本场人数不足");
        Click(window, 692, 627);
        if (controller.State.Requests[2].Status != RequestStatus.Rejected || controller.State.Requests[2].ResolutionNote != "本场人数不足")
            throw new InvalidOperationException("native request rejection failed");
        Click(window, 654, 314);
        if (controller.State.Requests[2].Status != RequestStatus.Pending) throw new InvalidOperationException("native request reopen failed");
        Console.WriteLine("PASS: native defer, reject with reason and reopen update request state");
        Click(window, 261, 101); Frame(window, 1100, 740);
        Set(window, "manualName", "登记观众"); Set(window, "manualWorld", "海幻沙"); Set(window, "manualQuery", "归途");
        Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "manual-request.png"));
        Click(window, 382, 464);
        if (controller.State.Requests.Count != 5 || controller.State.Requests.Last().RequesterName != "登记观众"
            || controller.State.Requests.Last().Query != "归途") throw new InvalidOperationException("native manual registration failed");
        Console.WriteLine("PASS: native manual registration modal creates a persistent request");
        Set(window, "requestFilter", 2); Frame(window, 1100, 740);
        Click(window, 100, 212); Click(window, 670, 360);
        Frame(window, 1100, 740);
        if (Get<Guid?>(window, "selectedEntryId") != controller.State.Requests[0].SetlistEntryId)
            throw new InvalidOperationException("native linked program navigation failed");
        Console.WriteLine("PASS: native arranged-request navigation selects the associated program");
        ImGui.GetIO().AddMousePosEvent(-100, -100); Frame(window, 1100, 740);
        SoftwareRenderer.Save(Path.Combine(output, "request-program.png"));
    }

    private static void Click(MainWindow window, float x, float y, int width = 1100, int height = 740)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(x, y); Frame(window, width, height);
        io.AddMouseButtonEvent(0, true); Frame(window, width, height);
        io.AddMouseButtonEvent(0, false); Frame(window, width, height);
    }

    private static void Drag(MainWindow window, Vector2 source, Vector2 destination)
    {
        var io = ImGui.GetIO();
        io.AddMousePosEvent(source.X, source.Y); Frame(window, 1100, 740);
        io.AddMouseButtonEvent(0, true); Frame(window, 1100, 740);
        io.AddMousePosEvent(source.X + 12, source.Y); Frame(window, 1100, 740);
        io.AddMousePosEvent(destination.X, destination.Y); Frame(window, 1100, 740); Frame(window, 1100, 740);
        io.AddMouseButtonEvent(0, false); Frame(window, 1100, 740);
    }

    private static void Frame(MainWindow window, int width, int height)
    {
        ImGui.GetIO().DisplaySize = new Vector2(width, height);
        ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new Vector2(width, height));
        ImGui.Begin("midibard2-深海回响特供版 · 自动点歌##Runtime", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        window.Draw(); ImGui.End(); ImGui.Render();
        if (ImGui.GetDrawData().TotalVtxCount == 0) throw new InvalidOperationException("blank ImGui frame");
    }
    private static void Set(object instance, string field, object value) => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Invoke(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
}
