using System.Numerics;
using BardStage.Core.Rooms;
using Dalamud.Bindings.ImGui;

namespace BardStage.Windows;

public sealed partial class MainWindow
{
    private Guid largeSong;
    private string largeUiError = "";
    private void LargeOperation(Action action)
    {
        try { action(); largeUiError = ""; }
        catch (Exception ex) { largeUiError = ex.Message; }
    }
    private int largeStep;
    private void DrawLargeEnsemble()
    {
        var large=controller.Room!.LargeEnsemble!;
        ImGui.TextColored(UiKit.Accent,"多人合奏 · 2～8 人");
        if(ImGui.Button(large.IsCaptain?"全员停止##largeStop":"停止本机并通知主控##largeStop")) large.StopAll();
        UiKit.RecordItem("largeStop");
        ImGui.SameLine(); ImGui.TextWrapped(large.Enabled?large.IsCaptain?"当前角色：主控":"当前角色：接收端":"当前未启用");
        ImGui.TextWrapped(large.Status);
        if(largeUiError!="") ImGui.TextColored(UiKit.Warning,largeUiError);
        for(var i=0;i<3;i++)
        {
            if(i>0) ImGui.SameLine();
            var label=new[]{"1 连接成员","2 选曲分配","3 下发开演"}[i];
            if(ImGui.RadioButton(label,largeStep==i)) { largeStep=i; ImGui.SetScrollY(0); }
            UiKit.RecordItem("largeStep"+i);
        }
        ImGui.Separator();
        if(largeStep==0) DrawLargeConnection(large);
        else if(largeStep==1) DrawLargeSong(large);
        else DrawLargePerformance(large);
    }
    private void DrawLargeConnection(RoomLargeEnsemble large)
    {
        ImGui.TextWrapped("全员使用 3.2.5.33，选择同一种连接方式并开启接收。保持同一小队、世界和场景，小队及参与演奏总人数均最多 8 人。");
        ImGui.BeginDisabled(large.Enabled || large.Busy);
        if(ImGui.RadioButton("跨电脑房间",!large.LocalMode)) LargeOperation(()=>large.SetLocalMode(false));
        UiKit.RecordItem("largeNetworkMode"); ImGui.SameLine();
        if(ImGui.RadioButton("同机多开（免房间）",large.LocalMode)) LargeOperation(()=>large.SetLocalMode(true));
        UiKit.RecordItem("largeLocalMode"); ImGui.EndDisabled();
        var enabled=large.Enabled;
        if(ImGui.Checkbox("开启本角色的多人合奏接收（实验）",ref enabled)) LargeOperation(()=>large.SetEnabled(enabled));
        UiKit.RecordItem("largeEnable");
        if(large.Context.Validate() is { } issue) ImGui.TextColored(UiKit.Warning,issue);
        if(large.LocalMode)
        {
            ImGui.TextWrapped("一台电脑无需建房。一个角色设为主控并勾选参与角色，其他角色等待安排。");
            DrawLocalEnsembleMembers(large);
        }
        else
        {
            ImGui.TextWrapped("主控先开启接收，再创建房间；演奏队员使用队员邀请码加入。队员无需导入歌曲。");
            ImGui.TextWrapped(controller.Room!.ConnectionStatus);
            if(ImGui.Button("主控：创建 / 管理房间")) { roomChoice=0; Navigate(Page.Room); }
            if(ImGui.Button("队员：输入邀请码")) { roomChoice=2; Navigate(Page.Room); }
        }
        ImGui.Separator();
        ImGui.BeginDisabled(!large.Enabled);
        if(ImGui.Button(large.IsCaptain?"下一步：选择歌曲与分配":"查看本机接收状态",new(-1,0))) largeStep=large.IsCaptain?1:2;
        UiKit.RecordItem("largeNext"); ImGui.EndDisabled();
    }
    private void DrawLargeSong(RoomLargeEnsemble large)
    {
        if(!large.IsCaptain)
        {
            ImGui.TextWrapped("歌曲和配器由主控下发。队员无需导入曲库，也无需修改自动分配开关。");
            if(ImGui.Button("查看接收与准备状态")) largeStep=2;
            return;
        }
        ImGui.TextWrapped("先选曲并读取轨道。默认自动分配；需要时展开手动配器，再进入下发开演。");
        var song=controller.State.Songs.FirstOrDefault(s=>s.Id==largeSong);
        ImGui.BeginDisabled(large.ControlIssue!=null || large.Busy);
        ImGui.SetNextItemWidth(-1);
        if(ImGui.BeginCombo("##largeSong",song?.Title??"从主控曲库选择 MIDI"))
        {
            foreach(var item in controller.State.Songs)
                if(ImGui.Selectable(item.Title+"##"+item.Id,item.Id==largeSong)) largeSong=item.Id;
            ImGui.EndCombo();
        }
        UiKit.RecordItem("largeSong");
        if(controller.State.Songs.Count==0)
        {
            ImGui.TextWrapped("主控曲库为空，请先导入 MIDI。");
            if(ImGui.Button("去曲库导入")) Navigate(Page.Library);
        }
        ImGui.BeginDisabled(song==null);
        if(ImGui.Button("读取歌曲轨道##largeInspect")) LargeOperation(()=>large.Inspect(song!.FilePath));
        UiKit.RecordItem("largeInspect"); ImGui.EndDisabled();
        var compensation=large.UseInstrumentCompensation;
        if(ImGui.Checkbox("乐器起音补偿（全员统一）",ref compensation)) LargeOperation(()=>large.SetInstrumentCompensation(compensation));
        UiKit.RecordItem("largeCompensation");
        ImGui.TextWrapped("通常保持开启；MIDI 已提前做过起音补偿时关闭。修改后需要重新下发。");
        ImGui.EndDisabled();
        if(large.ControlIssue is { } issue) ImGui.TextColored(UiKit.Warning,issue);
        if(large.Draft is not { } draft) return;
        ImGui.TextWrapped($"已读取 {System.IO.Path.GetFileName(draft.Path)} · {draft.Tracks.Count(t=>t.Enabled)} 条启用轨道 · {large.Participants.Length} 位参与者");
        ImGui.BeginDisabled(large.ControlIssue!=null || large.Busy);
        if(ImGui.Button("重新自动分配##largeAuto")) LargeOperation(large.AutoAssign);
        UiKit.RecordItem("largeAuto");
        var editor=ImGui.CollapsingHeader("检查 / 手动指定轨道与乐器");
        UiKit.RecordItem("largeEditor");
        if(editor)
        {
            for(var i=0;i<draft.Tracks.Length;i++)
            {
                var track=draft.Tracks[i]; var changed=false; ImGui.PushID(i);
                var active=track.Enabled;
                if(ImGui.Checkbox($"{i+1}. {track.Name}##track",ref active)) { track=track with { Enabled=active }; changed=true; }
                ImGui.SetNextItemWidth(-1);
                var selected=large.Participants.FirstOrDefault(m=>m.Cid==track.PerformerCid);
                if(ImGui.BeginCombo("##performer",selected?.Name??"未指定演奏人"))
                {
                    if(ImGui.Selectable("未指定",selected==null)) { track=track with { PerformerCid=0 }; changed=true; }
                    foreach(var member in large.Participants.OrderBy(m=>m.Group).ThenBy(m=>m.Cid))
                        if(ImGui.Selectable($"{member.Name} · 组 {member.Group+1}##{member.Cid}",member.Cid==track.PerformerCid))
                        { track=track with { PerformerCid=member.Cid }; changed=true; }
                    ImGui.EndCombo();
                }
                UiKit.RecordItem("largePerformer"+i);
                ImGui.SetNextItemWidth(Math.Max(100,ImGui.GetContentRegionAvail().X*.6f)); var instrument=(int)track.Instrument;
                if(ImGui.Combo("##instrument",ref instrument,large.Instruments.ToArray(),large.Instruments.Count))
                { track=track with { Instrument=(uint)instrument }; changed=true; }
                ImGui.SameLine(); ImGui.SetNextItemWidth(Math.Max(70,ImGui.GetContentRegionAvail().X-ImGui.CalcTextSize("移调").X-10));
                var transpose=track.Transpose;
                if(ImGui.InputInt("移调",ref transpose,0)) { track=track with { Transpose=Math.Clamp(transpose,-120,120) }; changed=true; }
                if(changed) { var index=i; LargeOperation(()=>large.Edit(index,track)); }
                ImGui.PopID(); ImGui.Separator();
            }
        }
        ImGui.EndDisabled();
        if(ImGui.Button("下一步：下发并检查全员准备",new(-1,0))) { largeStep=2; ImGui.SetScrollY(0); }
        UiKit.RecordItem("largeSongNext");
    }
    private void DrawLargePerformance(RoomLargeEnsemble large)
    {
        if(large.IsCaptain)
        {
            ImGui.TextWrapped("下发后等待每位成员载入歌曲、取出乐器。全员准备完成后，预约同一时刻开演。");
            if(large.Draft==null) ImGui.TextColored(UiKit.Warning,"还没有选曲：请先完成第 2 步。");
            ImGui.BeginDisabled(large.ControlIssue!=null || large.Busy || large.Draft==null);
            if(ImGui.Button("下发歌曲与分配##largeDistribute",new(-1,0))) LargeOperation(large.Distribute);
            UiKit.RecordItem("largeDistribute"); ImGui.EndDisabled();
            ImGui.BeginDisabled(large.Phase!=LargePhase.Ready);
            if(ImGui.Button("全员准备后预约开演（5 秒）##largeStart",new(-1,0))) LargeOperation(large.Start);
            UiKit.RecordItem("largeStart"); ImGui.EndDisabled();
        }
        else ImGui.TextWrapped("本机已开启接收后，等主控下发。无需再次选曲，也不要在游戏内另发起小队合奏。");
        if(large.PublishedInstrumentCompensation is { } compensated)
            ImGui.TextWrapped(compensated?"当前歌曲：起音补偿开启":"当前歌曲：起音补偿关闭");
        ImGui.Separator();
        ImGui.TextUnformatted($"参与演奏：{large.Participants.Length} 人 / 团队共 {large.Context.Members.Length} 人");
        foreach(var member in large.Participants.OrderBy(m=>m.Group).ThenBy(m=>m.Cid))
        {
            var r=large.Reports.FirstOrDefault(r=>r.Cid==member.Cid);
            var status=r==null?"等待连接":!r.Enabled?"尚未开启接收":r.Issue!=""?r.Issue:!r.ClockReady?"正在校时"
                :r.Finished?"已完成":r.Playing?"演奏中":r.Armed?"已确认预约":r.Ready?"歌曲与乐器已准备":"等待主控下发";
            ImGui.TextWrapped($"{member.Name}：{status}");
        }
        UiKit.RecordItem("largeRosterEnd");
        var details=ImGui.CollapsingHeader("连接与计时详情");
        UiKit.RecordItem("largeDiagnostics");
        if(details)
        {
            ImGui.TextWrapped("以下是插件调度与游戏接口调用记录，不是观众实听延迟。音符严重迟到超过 80 ms 会停止，需要重新下发；短卡顿会跳过已过期的音，保持后续时间轴。");
            foreach(var r in large.Reports.OrderBy(r=>r.Cid))
            {
                var name=large.Context.Members.FirstOrDefault(m=>m.Cid==r.Cid)?.Name??r.Cid.ToString();
                ImGui.TextWrapped($"{name} · 往返 {r.Rtt*1000:F0} ms · 校时波动 {r.Jitter*1000:F0} ms · 播放偏差 {r.Drift*1000:F0} ms");
                if(r.Output is { } timing && (r.StartLateMs!=null || timing.Dispatched!=0 || timing.Faults!=0))
                {
                    ImGui.TextWrapped($"起播迟到 {(r.StartLateMs is { } late ? late.ToString("F1")+" ms":"无")} · 发音最大迟到 {timing.MaxLatenessMs:F1} ms · 回调最长间隔 {timing.MaxCallbackGapMs:F1} ms · 发音异常 {timing.Faults}");
                    UiKit.RecordItem("largeTiming"+r.Cid);
                }
            }
        }
    }

    private void DrawLocalEnsembleMembers(RoomLargeEnsemble large)
    {
        var local = large.Local!;
        ImGui.BeginDisabled(!large.Enabled);
        if (!large.IsCaptain)
        {
            if (ImGui.Button("设为本机主控")) LargeOperation(large.BecomeLocalHost);
            UiKit.RecordItem("largeLocalHost");
            ImGui.TextWrapped(local.Connected ? "已由本机主控管理，等待选曲与分配。" : "等待本机主控选择本角色。");
        }
        else
        {
            if (ImGui.Button("释放本组角色")) LargeOperation(large.ReleaseLocalHost);
            UiKit.RecordItem("largeLocalRelease");
        }
        ImGui.EndDisabled();
        var expanded = ImGui.CollapsingHeader("本机角色与参与选择", ImGuiTreeNodeFlags.DefaultOpen);
        UiKit.RecordItem("largeLocalMembers");
        if (!expanded) return;
        foreach (var peer in local.Discovered.OrderBy(p => p.Context.SelfCid))
        {
            var member = peer.Context.Members.FirstOrDefault(m => m.Cid == peer.Context.SelfCid);
            if (member == null) continue;
            var chosen = local.Targets.Any(t => t.Node == peer.Node);
            var issue = local.CandidateIssue(peer);
            ImGui.BeginDisabled(!large.IsCaptain || large.Busy || peer.Node == local.Node || (!chosen && issue != null));
            if (ImGui.Checkbox($"{member.Name} · 世界 {member.World}##local{peer.Node}", ref chosen))
                LargeOperation(() => large.SelectLocalParticipant(peer.Node, chosen));
            UiKit.RecordItem("largeLocalMember" + member.Cid); ImGui.EndDisabled();
            if (issue != null) ImGui.TextWrapped(issue);
        }
        foreach (var target in local.Targets.Where(t => t.Node != local.Node && !local.Discovered.Any(p => p.Node == t.Node)))
        {
            var selected = true;
            ImGui.BeginDisabled(!large.IsCaptain || large.Busy);
            if (ImGui.Checkbox($"{large.Context.Members.FirstOrDefault(m => m.Cid == target.Cid)?.Name ?? target.Cid.ToString()}（离线）##local{target.Node}", ref selected))
                LargeOperation(() => large.SelectLocalParticipant(target.Node,false));
            ImGui.EndDisabled();
        }
        UiKit.RecordItem("largeLocalMembersEnd");
    }
}
