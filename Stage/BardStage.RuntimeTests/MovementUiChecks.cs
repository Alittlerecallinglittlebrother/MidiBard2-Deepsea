using System.Numerics;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using BardStage.Windows;
using Dalamud.Bindings.ImGui;
using HexaGen.Runtime;

internal static unsafe partial class RuntimeUi
{
    internal static void RunMovementUi(string output)
    {
        Directory.CreateDirectory(output);
        using var native = new NativeLibraryContext(Path.Combine(AppContext.BaseDirectory, "cimgui.dll"));
        ImGui.InitApi(native); var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO(); io.IniFilename = null; io.DeltaTime = 1f / 60;
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, default, io.Fonts.GetGlyphRangesChineseFull());
            if (!io.Fonts.Build()) throw new InvalidOperationException("font build failed");
            ImGui.StyleColorsDark();
            var members = Enumerable.Range(1,8).Select(i => new MovementMember((ulong)i, i == 1 ? "队长 · 断水剑" : $"演奏人 {i}",
                new((i - 4.5f)*2,0,0),0,true)).ToArray();
            ulong self = 1;
            using var controller = new StageController(Path.Combine(output, "ui-" + Guid.NewGuid().ToString("N")));
            controller.Room = new(controller);
            using var driver = new MovementRuntimeChecks.Driver(() => new(7,self,1,new(100,10),members));
            using var movement = new RoomMovementCoordinator(controller.Room, driver, _ => 0, controller.DataDirectory);
            controller.Room.Movement = movement;
            controller.Room.Create(0); movement.Tick();
            using var window = new MainWindow(controller);
            UiKit.ItemBounds = (id, min, max) => items[id] = (min,max);
            Frame(window,1100,740); Frame(window,1100,740); ClickItem(window,"movementTab");
            Frame(window,1100,740); ClickItem(window,"movementEnable");
            MovementRuntimeChecks.Check(movement.Enabled,"native movement checkbox enables local receiving");
            ClickItem(window,"formationNew");
            Frame(window,1100,740); Frame(window,1100,740);
            var preset = movement.Store.Presets.Single();
            MovementRuntimeChecks.Check(preset.Slots.Count == 8,"native new formation binds eight named party members");
            SoftwareRenderer.Save(Path.Combine(output,"movement-editor-1100.png"));
            io.AddMousePosEvent(600,480); io.AddMouseWheelEvent(0,-9);
            Frame(window,1100,740); Frame(window,1100,740);
            var canvas = items["formationCanvas"];
            var center=(canvas.Item1+canvas.Item2)/2;
            var pixels=Math.Min(canvas.Item2.X-canvas.Item1.X,canvas.Item2.Y-canvas.Item1.Y)/24;
            var start = center + new Vector2(preset.Slots[3].Right, -preset.Slots[3].Forward)*pixels;
            var old = preset.Slots[3];
            io.AddMousePosEvent(start.X,start.Y); Frame(window,1100,740);
            io.AddMouseButtonEvent(0,true); Frame(window,1100,740);
            io.AddMousePosEvent(start.X+25,start.Y-15); Frame(window,1100,740);
            io.AddMouseButtonEvent(0,false); Frame(window,1100,740);
            MovementRuntimeChecks.Check(Math.Abs(preset.Slots[3].Right-old.Right)>0.5f && preset.Slots[3].Forward>0.5f,
                "native drag edits a character station and saves it");
            var copy = new FormationStore(controller.DataDirectory); copy.Load(1);
            MovementRuntimeChecks.Check(copy.Presets.Single().Slots[3] == preset.Slots[3],"dragged formation reloads from disk");
            SoftwareRenderer.Save(Path.Combine(output,"movement-editor-1100-scrolled.png"));
            io.FontGlobalScale=1.4f;
            Frame(window,760,540); Frame(window,760,540);
            io.AddMousePosEvent(400,330); io.AddMouseWheelEvent(0,60);
            Frame(window,760,540); Frame(window,760,540);
            SoftwareRenderer.Save(Path.Combine(output,"movement-editor-760-top.png"));
            ClickItem(window,"movementLocalStop",760,540);
            MovementRuntimeChecks.Check(!movement.Enabled,"compact local stop releases movement and disables receiving");
            for (var scroll = 0; scroll < 3; scroll++)
            {
                io.AddMousePosEvent(450,360); io.AddMouseWheelEvent(0,-20);
                Frame(window,760,540); Frame(window,760,540);
            }
            SoftwareRenderer.Save(Path.Combine(output,"movement-editor-760-bottom.png"));
            var apply=items["formationApply"];
            var panel=items["movementPanel"];
            Console.WriteLine($"UI geometry: apply={apply}, panel={panel}");
            MovementRuntimeChecks.Check(apply.Item1.Y>panel.Item1.Y && apply.Item2.Y<panel.Item2.Y,"compact formation submit is reachable by scrolling");

            self = 2; movement.Tick();
            io.AddMousePosEvent(450,340); io.AddMouseWheelEvent(0,60);
            Frame(window,760,540); Frame(window,760,540);
            SoftwareRenderer.Save(Path.Combine(output,"movement-viewer-760.png"));
            MovementRuntimeChecks.Check(!movement.IsLeader && !movement.Enabled,"character switch resets receiving and renders follower controls");
        }
        finally { UiKit.ItemBounds=null; ImGui.DestroyContext(context); }
    }
}
