using System.Numerics;
using System.Reflection;
using System.Text.Json;
using BardStage;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunNoticeChecks(string output, bool legacyRenderer = false)
    {
        Directory.CreateDirectory(output);
        var text = (string)typeof(MainWindow).GetField("PluginNotice", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
        var drawNotice = (Action)typeof(MainWindow).GetMethod("DrawPluginNotice", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate(typeof(Action));
        // Exact 3.2.5.19 implementation, kept only as a negative-control renderer.
        // Both renderers run through the same height/bounds/font/cursor assertions.
        if (legacyRenderer) drawNotice = () =>
        {
            ImGui.PushStyleColor(ImGuiCol.Text, UiKit.Muted);
            ImGui.TextWrapped(text);
            UiKit.RecordItem("pluginNotice");
            ImGui.PopStyleColor();
        };

        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native);
        var context = ImGui.CreateContext();
        var results = new List<object>();
        var failures = new List<string>();
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
            if (!io.Fonts.Build()) throw new InvalidOperationException("notice font atlas failed");
            ImGui.StyleColorsDark();
            ImGui.GetStyle().WindowRounding = 0;
            using var controller = new StageController(Path.Combine(Path.GetTempPath(), "notice-ui-" + Guid.NewGuid()));
            using var window = new MainWindow(controller);

            if (!legacyRenderer)
            {
                foreach (var scale in new[] { 1f, 1.4f, 2f })
                foreach (var width in new[] { 760, 900, 1100, 1400 })
                {
                    io.FontGlobalScale = scale;
                    var name = $"window-{width}-{scale * 100:0}pct";
                    var seen = 0;
                    Vector2 noticeMin = default, noticeMax = default;
                    UiKit.ItemBounds = (id, min, max) =>
                    {
                        if (id != "pluginNotice") return;
                        seen++;
                        noticeMin = min; noticeMax = max;
                        var fontSize = ImGui.GetFontSize();
                        var pos = ImGui.GetWindowPos();
                        var size = ImGui.GetWindowSize();
                        Verify(max.Y - min.Y <= MathF.Ceiling(fontSize) + .1f, name, $"notice is more than one line: {max.Y - min.Y:F3} > {fontSize:F3}");
                        Verify(min.X >= pos.X && max.X <= pos.X + size.X + .1f, name, "notice exceeds window bounds");
                        Verify(Math.Abs(fontSize - 17 * scale) < .1f, name, "notice changed the global font size");
                        results.Add(new { name, fontSize, minX = min.X, maxX = max.X, height = max.Y - min.Y });
                    };
                    Frame(window, width, 740); seen = 0; Frame(window, width, 740);
                    Verify(seen == 1, name, $"expected one notice item, got {seen}");
                    Console.WriteLine($"PASS: {name} notice bounds and one-line height");
                    if (width == 1100 && scale == 1)
                    {
                        var screenshot = Path.Combine(output, "notice-normal-1100-100pct.png");
                        SoftwareRenderer.Save(screenshot);
                        using var source = new System.Drawing.Bitmap(screenshot);
                        var rectangle = System.Drawing.Rectangle.FromLTRB(Math.Max(0, (int)noticeMin.X - 6), Math.Max(0, (int)noticeMin.Y - 6),
                            Math.Min(width, (int)MathF.Ceiling(noticeMax.X) + 6), (int)MathF.Ceiling(noticeMax.Y) + 6);
                        using var strip = source.Clone(rectangle, source.PixelFormat);
                        strip.Save(Path.Combine(output, "notice-strip.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    if (width == 760 && scale == 1.4f) SoftwareRenderer.Save(Path.Combine(output, "notice-narrow-760-140pct.png"));
                }
            }

            foreach (var scale in new[] { 1f, 1.4f, 2f })
            {
                io.FontGlobalScale = scale;
                // Measure at the native current font size in a live ImGui frame.
                io.DisplaySize = new Vector2(2400, 360);
                ImGui.NewFrame(); ImGui.Begin("measure-notice");
                var naturalWidth = ImGui.CalcTextSize(text).X;
                var lastGlyph = ImGui.CalcTextSize(text[^1..]).X;
                ImGui.End(); ImGui.Render();
                foreach (var available in new[] { 744f, naturalWidth - lastGlyph * .5f, naturalWidth - .25f, naturalWidth, naturalWidth + .25f })
                {
                    var label = $"isolated-{scale * 100:0}pct-avail-{available:F2}";
                    Probe(available, label, false); Probe(available, label, true);
                    if (scale == 1 && Math.Abs(available - (naturalWidth - lastGlyph * .5f)) < .01f)
                        SoftwareRenderer.Save(Path.Combine(output, legacyRenderer ? "notice-legacy-last-glyph-wrap.png" : "notice-last-glyph-boundary.png"));
                }
            }
            var summary = new { renderer = legacyRenderer ? "3.2.5.19 TextWrapped negative control" : "production DrawPluginNotice", results, failures };
            File.WriteAllText(Path.Combine(output, "notice-check-results.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            if (failures.Count != 0) throw new InvalidOperationException($"Notice checks failed ({failures.Count}): {string.Join("; ", failures.Take(5))}");
            Console.WriteLine($"PASS: notice-only native checks completed ({results.Count} observations); no game playback invoked");

            void Verify(bool ok, string label, string reason)
            {
                if (!ok && !failures.Contains(label + ": " + reason))
                {
                    failures.Add(label + ": " + reason);
                    Console.WriteLine("FAIL: " + label + ": " + reason);
                }
            }

            void Probe(float available, string label, bool record)
            {
                var scale = io.FontGlobalScale;
                var screenWidth = Math.Max(1100, (int)MathF.Ceiling(available) + 80);
                io.DisplaySize = new Vector2(screenWidth, 260);
                ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("说明文字单行边界检查##Notice", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar);
                ImGui.TextUnformatted($"{(legacyRenderer ? "旧版本 TextWrapped" : "修复后单行显示")} | 字体 {scale * 100:0}% | 可用宽度 {available:F2}px");
                ImGui.Separator();
                var right = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - ImGui.GetStyle().WindowPadding.X;
                var start = new Vector2(right - available, ImGui.GetCursorScreenPos().Y + 12);
                ImGui.SetCursorScreenPos(start);
                var fontBefore = ImGui.GetFont();
                var fontSize = ImGui.GetFontSize();
                var color = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
                var list = ImGui.GetWindowDrawList();
                var beginVertex = list.VtxBuffer.Size;
                Vector2 itemMin = default, itemMax = default;
                var seen = 0;
                UiKit.ItemBounds = (id, min, max) => { if (id == "pluginNotice") { itemMin = min; itemMax = max; seen++; } };
                drawNotice();
                var after = ImGui.GetCursorScreenPos();
                var vertices = list.VtxBuffer.Size - beginVertex;
                var glyphMin = new Vector2(float.MaxValue); var glyphMax = new Vector2(float.MinValue);
                for (var i = beginVertex; i < list.VtxBuffer.Size; i++)
                {
                    glyphMin = Vector2.Min(glyphMin, list.VtxBuffer[i].Pos);
                    glyphMax = Vector2.Max(glyphMax, list.VtxBuffer[i].Pos);
                }
                if (record)
                {
                    var failuresBefore = failures.Count;
                    Verify(seen == 1, label, "notice is missing its registered item");
                    Verify(itemMax.Y - itemMin.Y <= MathF.Ceiling(fontSize) + .1f, label, $"notice wrapped: height={itemMax.Y - itemMin.Y:F2}; font={fontSize:F2}");
                    Verify(glyphMax.Y - glyphMin.Y <= fontSize + 1, label, "glyph geometry spans more than one line");
                    Verify(itemMin.X >= start.X - .1f && itemMax.X <= right + .1f, label, "item exceeds available width");
                    // Glyph quads include font-atlas padding/negative bearings, and ImGui
                    // snaps the origin down to a pixel. Check the actual window on the left
                    // and available content boundary on the right, rather than rejecting
                    // a valid fractional negative bearing before the logical text origin.
                    Verify(glyphMin.X >= ImGui.GetWindowPos().X && glyphMax.X <= right + .1f, label,
                        $"glyph geometry exceeds window/content bounds: left={glyphMin.X:F3}; right={glyphMax.X:F3}; limit={right:F3}");
                    Verify(vertices == text.Length * 4, label, $"incomplete text: expected {text.Length * 4} glyph vertices, got {vertices}");
                    Verify(ImGui.GetFont().Handle == fontBefore.Handle && Math.Abs(ImGui.GetFontSize() - fontSize) < .001f, label, "font state leaked to subsequent controls");
                    Verify(ImGui.GetStyle().Colors[(int)ImGuiCol.Text] == color, label, "text color leaked to subsequent controls");
                    Verify(Math.Abs(after.X - ImGui.GetWindowPos().X - ImGui.GetStyle().WindowPadding.X) < .1f, label, "following row cursor X is not reset to the content origin");
                    Verify(after.Y >= itemMax.Y && after.Y <= start.Y + MathF.Ceiling(fontSize) + ImGui.GetStyle().ItemSpacing.Y + .1f, label, "following cursor advanced by more than one line");
                    results.Add(new { name = label, fontSize, availableWidth = available, itemHeight = itemMax.Y - itemMin.Y, glyphHeight = glyphMax.Y - glyphMin.Y, vertices, firstGlyphLeft = glyphMin.X, lastGlyphRight = glyphMax.X, contentRight = right });
                    Console.WriteLine($"{(failures.Count == failuresBefore ? "PASS" : "FAIL")}: {label} itemHeight={itemMax.Y - itemMin.Y:F2}; glyphHeight={glyphMax.Y - glyphMin.Y:F2}; vertices={vertices}; fontUnchanged={ImGui.GetFontSize():F2}");
                }
                ImGui.Separator();
                ImGui.TextUnformatted("后续控件保持原字体大小，说明文字不占用第二行");
                ImGui.Button("演奏模式 / 播放控件不缩小");
                ImGui.End(); ImGui.Render();
            }
        }
        finally { UiKit.ItemBounds = null; UiKit.IconFont = null; ImGui.DestroyContext(context); }
    }
}
