using System.Numerics;
using BardStage.Core.Rooms;
using Dalamud.Bindings.ImGui;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private Guid selectedFormation;
    private int draggedSlot = -1;
    private float formationZoom = 12;
    private string movementUiError = "";

    private void MovementOperation(Action operation)
    {
        try { operation(); movementUiError = ""; }
        catch (Exception ex) { movementUiError = ex.Message; }
    }
    private void DrawMovement()
    {
        var movement = controller.Room!.Movement!;
        var enabled = movement.Enabled;
        if (ImGui.Checkbox("允许本机接收队长移动指令", ref enabled)) movement.SetEnabled(enabled);
        UiKit.RecordItem("movementEnable");
        UiKit.Tip("每次插件加载后默认关闭。停止本机、按 Esc 或手动移动可退出本次指令。");
        if (ImGui.Button("停止本机##movementLocalStop")) movement.StopLocal();
        UiKit.RecordItem("movementLocalStop");
        ImGui.SameLine();
        ImGui.BeginDisabled(!movement.IsLeader);
        if (ImGui.Button("全员停止##movementStop")) movement.StopAll();
        UiKit.RecordItem("movementStop");
        ImGui.EndDisabled();
        ImGui.BeginDisabled(movement.ControlIssue != null || movement.Busy);
        if (ImGui.Button("开始跟随队长##movementFollow", new Vector2(-1, 0))) MovementOperation(movement.Follow);
        UiKit.RecordItem("movementFollow");
        ImGui.EndDisabled();
        if (movement.ControlIssue is { } issue) ImGui.TextWrapped(issue);
        ImGui.TextWrapped(movement.Status);
        if (movementUiError != "") ImGui.TextColored(UiKit.Warning, movementUiError);
        ImGui.TextWrapped("先跟随到演出地点，停止后再列队。首版仅支持同场景地面移动，不会绕开障碍物。");
        ImGui.Separator();

        if (ImGui.CollapsingHeader("队员状态", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var member in movement.Snapshot.Members)
            {
                var state = movement.Reports.FirstOrDefault(r => r.Cid == member.Cid);
                var label = state == null ? "等待连接 / 升级新版" : !state.Enabled ? "未开启移动接收"
                    : state.Issue != "" ? state.Issue : state.State + (state.Detail != "" ? " · " + state.Detail : "");
                ImGui.TextWrapped($"{member.Name}：{label}");
            }
        }
        if (!movement.IsLeader)
        {
            ImGui.TextWrapped("队长负责编辑和下发队形；你可以随时停止本机。");
            return;
        }
        ImGui.Separator();
        var store = movement.Store;
        if (store.Error != null) { ImGui.TextWrapped(store.Error); return; }
        ImGui.BeginDisabled(movement.Busy);
        var preset = store.Presets.FirstOrDefault(p => p.Id == selectedFormation) ?? store.Presets.FirstOrDefault();
        if (preset != null) selectedFormation = preset.Id;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##formationPresets", preset?.Name ?? "尚未保存队形"))
        {
            foreach (var value in store.Presets)
                if (ImGui.Selectable(value.Name + "##" + value.Id, value.Id == selectedFormation))
                { selectedFormation = value.Id; preset = value; }
            ImGui.EndCombo();
        }
        if (ImGui.Button("新建队形")) MovementOperation(() =>
        {
            if (store.Presets.Count >= 50) throw new InvalidOperationException("最多保存 50 个队形");
            var value = new FormationPreset { Name = "队形 " + (store.Presets.Count + 1),
                Slots = movement.Snapshot.Members.Select((m, i) => new FormationSlot(m.Cid, m.Name, (i - (movement.Snapshot.Members.Length - 1) / 2f) * 2, 0, 0)).ToList() };
            value.Validate(); store.Presets.Add(value); selectedFormation = value.Id; store.Save();
        });
        UiKit.RecordItem("formationNew");
        ImGui.SameLine();
        if (ImGui.Button("保存当前站位")) MovementOperation(() =>
        {
            if (store.Presets.Count >= 50) throw new InvalidOperationException("最多保存 50 个队形");
            var value = movement.CaptureFormation("现场站位 " + (store.Presets.Count + 1));
            store.Presets.Add(value); selectedFormation = value.Id; store.Save();
        });
        ImGui.SameLine();
        ImGui.BeginDisabled(preset == null);
        if (ImGui.Button("复制")) MovementOperation(() =>
        {
            if (store.Presets.Count >= 50) throw new InvalidOperationException("最多保存 50 个队形");
            var copy = new FormationPreset { Name = preset!.Name[..Math.Min(preset.Name.Length, 75)] + " 副本", Slots = [.. preset.Slots] };
            store.Presets.Add(copy); selectedFormation = copy.Id; store.Save();
        });
        ImGui.SameLine();
        if (ImGui.Button("删除")) ImGui.OpenPopup("确认删除队形");
        if (ImGui.BeginPopup("确认删除队形"))
        {
            ImGui.TextUnformatted("删除当前保存的队形？");
            if (ImGui.Button("确认删除")) { MovementOperation(() => { store.Presets.Remove(preset!); store.Save(); }); ImGui.CloseCurrentPopup(); }
            ImGui.SameLine(); if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.EndDisabled();
        if (preset != null)
        {
            var name = preset.Name; ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##formationName", ref name, 80)) preset.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit()) MovementOperation(store.Save);
            if (ImGui.Button("横排")) SetFormationShape(preset, 0, store);
            ImGui.SameLine(); if (ImGui.Button("弧形")) SetFormationShape(preset, 1, store);
            ImGui.SameLine(); if (ImGui.Button("双排")) SetFormationShape(preset, 2, store);
            ImGui.SameLine(); if (ImGui.Button("保存修改")) MovementOperation(store.Save);
            ImGui.TextWrapped("拖动圆点调整站位。上方为队长前方，位置以米为单位；下发时以队长当前站位和朝向为基准。");
            DrawFormationCanvas(preset, store, movement.Busy);
            if (ImGui.BeginTable("formationMembers", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("演奏人", ImGuiTableColumnFlags.WidthStretch, 2.2f);
                ImGui.TableSetupColumn("左右", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("前后", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("朝向°", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableHeadersRow();
                for (var i = 0; i < preset.Slots.Count; i++)
                {
                    var index = i; var slot = preset.Slots[i]; ImGui.PushID(i); ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    if (ImGui.BeginCombo("##person", $"{i + 1}. {slot.Name}"))
                    {
                        foreach (var member in movement.Snapshot.Members)
                            if (ImGui.Selectable(member.Name, slot.Cid == member.Cid))
                            {
                                var other = preset.Slots.FindIndex(s => s.Cid == member.Cid);
                                if (other >= 0) preset.Slots[other] = preset.Slots[other] with { Cid = slot.Cid, Name = slot.Name };
                                slot = slot with { Cid = member.Cid, Name = member.Name };
                                preset.Slots[index] = slot; MovementOperation(store.Save);
                            }
                        ImGui.EndCombo();
                    }
                    var x = slot.Right; var z = slot.Forward; var facing = slot.FacingDegrees;
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    var changed = ImGui.DragFloat("##x", ref x, 0.05f, -20, 20, "%.2f", ImGuiSliderFlags.AlwaysClamp);
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    changed |= ImGui.DragFloat("##z", ref z, 0.05f, -20, 20, "%.2f", ImGuiSliderFlags.AlwaysClamp);
                    ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
                    changed |= ImGui.DragFloat("##facing", ref facing, 1, -180, 180, "%.0f", ImGuiSliderFlags.AlwaysClamp);
                    if (changed) { preset.Slots[i] = slot with { Right = x, Forward = z, FacingDegrees = facing }; MovementOperation(store.Save); }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.BeginDisabled(movement.ControlIssue != null);
            if (ImGui.Button("统一下发队形", new Vector2(-1, 0))) MovementOperation(() => { store.Save(); movement.Apply(preset); });
            UiKit.RecordItem("formationApply");
            ImGui.Dummy(new Vector2(0, 12 * UiKit.Scale));
            ImGui.EndDisabled();
        }
        ImGui.EndDisabled();
    }
    private void SetFormationShape(FormationPreset preset, int shape, FormationStore store)
    {
        for (var i = 0; i < preset.Slots.Count; i++)
        {
            var center = (preset.Slots.Count - 1) / 2f;
            var angle = (i - center) * 0.23f;
            preset.Slots[i] = preset.Slots[i] with {
                Right = shape == 1 ? 8 * MathF.Sin(angle) : shape == 2 ? (i % 4 - Math.Min(3, center) / 2f) * 2 : (i - center) * 2,
                Forward = shape == 1 ? 8 * (MathF.Cos(angle) - 1) : shape == 2 ? -(i / 4) * 2 : 0,
                FacingDegrees = 0 };
        }
        MovementOperation(store.Save);
    }
    private void DrawFormationCanvas(FormationPreset preset, FormationStore store, bool busy)
    {
        ImGui.SetNextItemWidth(180 * UiKit.Scale);
        ImGui.SliderFloat("视野范围（米）", ref formationZoom, 6, 24, "%.0f");
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 250 * UiKit.Scale);
        ImGui.InvisibleButton("formationCanvas", size);
        UiKit.RecordItem("formationCanvas");
        var hovered = ImGui.IsItemHovered(); var mouse = ImGui.GetIO().MousePos;
        var center = origin + size / 2; var pixels = Math.Min(size.X, size.Y) / (formationZoom * 2);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, 0xFF202B38);
        draw.PushClipRect(origin, origin + size, true);
        for (var i = -(int)formationZoom; i <= formationZoom; i += 2)
        {
            draw.AddLine(new(center.X + i * pixels, origin.Y), new(center.X + i * pixels, origin.Y + size.Y), 0x553D536B);
            draw.AddLine(new(origin.X, center.Y + i * pixels), new(origin.X + size.X, center.Y + i * pixels), 0x553D536B);
        }
        draw.AddLine(center + new Vector2(0, 10), center - new Vector2(0, 26), 0xFFB5B5B5, 2);
        draw.AddText(origin + new Vector2(8, 6), 0xFFBBBBBB, "队长前方");
        if (!busy && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            draggedSlot = -1; var distance = 22f * UiKit.Scale;
            for (var i = 0; i < preset.Slots.Count; i++)
            {
                var point = center + new Vector2(preset.Slots[i].Right, -preset.Slots[i].Forward) * pixels;
                if (Vector2.Distance(mouse, point) < distance) { draggedSlot = i; distance = Vector2.Distance(mouse, point); }
            }
        }
        if (!busy && draggedSlot >= 0 && draggedSlot < preset.Slots.Count && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            preset.Slots[draggedSlot] = preset.Slots[draggedSlot] with {
                Right = Math.Clamp((mouse.X - center.X) / pixels, -20, 20),
                Forward = Math.Clamp((center.Y - mouse.Y) / pixels, -20, 20) };
        }
        if (draggedSlot >= 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        { draggedSlot = -1; MovementOperation(store.Save); }
        for (var i = 0; i < preset.Slots.Count; i++)
        {
            var slot = preset.Slots[i]; var point = center + new Vector2(slot.Right, -slot.Forward) * pixels;
            draw.AddCircleFilled(point, 12 * UiKit.Scale, i == draggedSlot ? 0xFF52D6FF : 0xFFB78239);
            var a = slot.FacingDegrees * MathF.PI / 180;
            draw.AddLine(point, point + new Vector2(MathF.Sin(a), -MathF.Cos(a)) * 21 * UiKit.Scale, 0xFFEEEEEE, 2);
            draw.AddText(point - new Vector2(4, 8) * UiKit.Scale, 0xFFFFFFFF, (i + 1).ToString());
        }
        draw.PopClipRect();
    }
}
