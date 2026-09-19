using System.Numerics;
using BardStage;
using BardStage.Core;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunLibrarySearchRegressionChecks(string output)
    {
        Directory.CreateDirectory(output);
        var scratch = Path.Combine(Path.GetTempPath(), "library-search-regression-" + Guid.NewGuid());
        Directory.CreateDirectory(scratch);
        var good = Path.Combine(scratch, "good.mid");
        var other = Path.Combine(scratch, "other.mid");
        var broken = Path.Combine(scratch, "broken.mid");
        LibrarySyncRegressionChecks.WriteMidi(good, 60);
        LibrarySyncRegressionChecks.WriteMidi(other, 62);
        File.WriteAllText(broken, "invalid MIDI for repeat-import focus regression");
        using var controller = new StageController(Path.Combine(scratch, "data"));
        controller.Import([good, other]);
        LibrarySyncRegressionChecks.Complete(controller);
        controller.Change(s =>
        {
            s.Songs[0].Title = "中文检索目标曲";
            s.Songs[0].Aliases = ["searchcontinuity"];
            s.Songs[1].Title = "另一首曲目";
        });
        controller.EnsureAutomaticQueue();
        var firstId = controller.State.Songs[0].Id;
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            var icons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/dalamudAssets/dev/UIRes/FontAwesomeFreeSolid.otf");
            if (File.Exists(icons))
            {
                ushort* range = stackalloc ushort[] { 0xE000, 0xF8FF, 0 };
                var iconFont = io.Fonts.AddFontFromFileTTF(icons, 17, default, range);
                UiKit.IconFont = () => iconFont;
            }
            if (!io.Fonts.Build()) throw new InvalidOperationException("library-search font atlas failed");
            ImGui.StyleColorsDark();
            ImGui.GetStyle().WindowRounding = 0;
            var visibleSongs = new HashSet<string>();
            var searchSeen = false;
            var searchActive = false;
            var confirmationOpen = false;
            UiKit.ItemBounds = (id, min, max) =>
            {
                items[id] = (min, max);
                if (id == "librarySearch") { searchSeen = true; searchActive = ImGui.IsItemActive(); }
                if (id.StartsWith("librarySong:", StringComparison.Ordinal)) visibleSongs.Add(id);
                // This lookup requires a live current window; never call it after ImGui.Render().
                if (id == "pluginNotice") confirmationOpen = ImGui.IsPopupOpen("确认操作##BardStage");
            };
            using var window = new MainWindow(controller);
            Render(); Render();
            Click(window, 93, 73); Render(); Render();
            Verify(searchSeen && visibleSongs.Count == 2, "local library search and both songs render before synchronization");
            ClickItem(window, "librarySearch"); Render();
            Verify(searchActive, "real mouse click focuses local library search");

            var expected = "";
            foreach (var character in "searchcontinuity")
            {
                Verify(controller.SynchronizePlayerLibrary([good, other, broken], force: true), "forced background synchronization starts for typing frame");
                io.AddInputCharacter(character);
                expected += character;
                Render();
                Verify(controller.IsBusy && searchActive && Get<string>(window, "search") == expected,
                    "native typing retains focus and text during busy synchronization: " + expected);
                LibrarySyncRegressionChecks.Complete(controller);
                Render();
                Verify(!controller.IsBusy && controller.StatusIsError && searchActive && Get<string>(window, "search") == expected,
                    "native typing retains focus after synchronization warning: " + expected);
                Verify(visibleSongs.SetEquals(["librarySong:" + firstId]), "alias prefix filters the intended row during repeated synchronization");
            }
            SoftwareRenderer.Save(Path.Combine(output, "library-search-after-sync.png"));
            io.AddInputCharacter('X'); Render();
            Verify(searchSeen && searchActive && visibleSongs.Count == 0 && Get<string>(window, "search") == expected + "X",
                "zero-result search keeps a live focused input without rebuilding or clearing it");
            SoftwareRenderer.Save(Path.Combine(output, "library-search-no-results.png"));
            ReplaceSearch("检索");
            Verify(searchActive && visibleSongs.SetEquals(["librarySong:" + firstId]), "native Unicode input finds the Chinese song title after no matches");

            controller.Change(s => s.RequestSettings.PlaybackMode = QueuePlaybackMode.Ensemble);
            controller.LocalEnsembleControlIssue = () => "当前小队队长已变更";
            Render();
            Verify(!controller.CanEditQueue && searchActive, "leader-authority loss preserves local search focus");
            ReplaceSearch("另一首");
            Verify(searchActive && visibleSongs.SetEquals(["librarySong:" + controller.State.Songs[1].Id]),
                "read-only ensemble follower can continue typing and filtering local library");
            ClickItem(window, "deleteLibrarySong:" + controller.State.Songs[1].Id); Render();
            Verify(!confirmationOpen && !Get<bool>(window, "openConfirm") && controller.State.Songs.Count == 2,
                "read-only follower search does not enable library deletion");
            ClickItem(window, "librarySong:" + controller.State.Songs[1].Id); Render();
            Verify(Get<Guid?>(window, "selectedSongId") == controller.State.Songs[1].Id,
                "read-only follower may inspect filtered local song without gaining write authority");
            SoftwareRenderer.Save(Path.Combine(output, "library-search-follower.png"));

            io.FontGlobalScale = 1.4f;
            Render(760, 540); Render(760, 540);
            Verify(searchSeen, "library search remains present at 760x540 and 140 percent font scale");
            SoftwareRenderer.Save(Path.Combine(output, "library-search-small-140.png"));
            Console.WriteLine("PASS: native library-search regression completed; real ImGui mouse/key input, no game invoked");

            void Render(int width = 1100, int height = 740)
            {
                visibleSongs.Clear(); searchSeen = false; searchActive = false;
                Frame(window, width, height);
            }

            void ReplaceSearch(string text)
            {
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Render();
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false);
                foreach (var character in text) io.AddInputCharacter(character);
                Render(); Render();
                Verify(Get<string>(window, "search") == text, "native select-all replacement updates search to " + text);
            }
        }
        finally { UiKit.ItemBounds = null; UiKit.IconFont = null; items.Clear(); ImGui.DestroyContext(context); }

        static void Verify(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Console.WriteLine("PASS: " + message);
        }
    }
}
