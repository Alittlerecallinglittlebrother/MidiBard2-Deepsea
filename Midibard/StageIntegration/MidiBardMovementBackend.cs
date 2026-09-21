#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using BardStage;
using BardStage.Core;
using BardStage.Core.Rooms;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using MidiBard.Managers.Ipc;

namespace MidiBard.StageIntegration;

/// <summary>Ground input only; no position writes, game setting changes, background keys or native follow ownership.</summary>
internal sealed unsafe class MidiBardMovementBackend : IRoomMovementBackend
{
    private const string WalkSignature = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";
    private const string InputOneSignature = "E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C";
    private const string InputTwoSignature = "E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F";
    private delegate void WalkInput(void* self, float* left, float* forward, float* turn, byte* strafe, byte* unknown, byte additive);
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool InputAllowed(void* self);
    private Hook<WalkInput>? hook;
    private InputAllowed? allowedOne, allowedTwo;
    private Vector3? target;
    private float tolerance;
    private long lastDrive, manualUntil;
    private bool enabled, disposed;
    private string? fault;

    public string? Enable()
    {
        if (disposed) return "移动模块已卸载";
        try
        {
            if (hook == null)
            {
                // Dalamud resolves relative E8 call signatures to their function entry.
                var walk = api.SigScanner.ScanText(WalkSignature);
                var one = api.SigScanner.ScanText(InputOneSignature);
                var two = api.SigScanner.ScanText(InputTwoSignature);
                if (walk == 0 || one == 0 || two == 0 || walk == one || walk == two || one == two)
                    throw new InvalidOperationException("移动接口签名未匹配");
                allowedOne = Marshal.GetDelegateForFunctionPointer<InputAllowed>(one);
                allowedTwo = Marshal.GetDelegateForFunctionPointer<InputAllowed>(two);
                hook = api.GameInteropProvider.HookFromAddress<WalkInput>(walk, Detour);
            }
            target = null; fault = null; manualUntil = 0; hook.Enable(); enabled = true;
            return null;
        }
        catch (Exception ex)
        {
            enabled = false; target = null;
            hook?.Dispose(); hook = null;
            api.PluginLog.Error(ex, "Movement input initialization failed");
            return "移动接口不可用，本机不会移动：" + ex.Message;
        }
    }
    public void Disable() { target = null; enabled = false; hook?.Disable(); }
    public MovementSnapshot Capture()
    {
        var self = api.ObjectTable.LocalPlayer;
        var cid = api.Player.ContentId;
        var party = api.PartyList.ToArray();
        var members = party.Select(p =>
        {
            var character = p.GameObject;
            return new MovementMember(p.ContentId, p.Name.TextValue, character != null
                ? MovementPosition.From(character.Position) : new(0, 0, 0), character?.Rotation ?? 0, character != null);
        }).ToArray();
        var reason = BlockReason();
        var manual = Stopwatch.GetTimestamp() < manualUntil || api.KeyState[VirtualKey.ESCAPE];
        return new(api.PartyList.PartyId, cid, api.PartyList.GetPartyLeader()?.ContentId ?? 0,
            new(api.ClientState.TerritoryType, self?.CurrentWorld.RowId ?? 0), members, reason, manual);
    }
    private string? BlockReason()
    {
        if (fault != null) return fault;
        if (!api.ClientState.IsLoggedIn || api.ObjectTable.LocalPlayer == null) return "等待角色登录";
        if (MidiBard.SlaveMode) return "请在本机独立演奏端使用移动";
        if (MidiBard.IsPlaying || MidiBard.AgentMetronome.EnsembleModeRunning || MidiBard.CurrentInstrument != 0
            || PartyChatCommand.IsLoading || api.Condition[ConditionFlag.Performing])
            return "请停止演奏并收起乐器后移动";
        if (api.ObjectTable.LocalPlayer.IsDead) return "角色无法移动";
        if (api.Condition[ConditionFlag.BetweenAreas] || api.Condition[ConditionFlag.BetweenAreas51])
            return "切换场景中";
        if (api.Condition[ConditionFlag.InCombat] || api.Condition[ConditionFlag.Casting]
            || api.Condition[ConditionFlag.Occupied] || api.Condition[ConditionFlag.Occupied30]
            || api.Condition[ConditionFlag.Occupied33] || api.Condition[ConditionFlag.Occupied38] || api.Condition[ConditionFlag.Occupied39]
            || api.Condition[ConditionFlag.OccupiedInEvent]
            || api.Condition[ConditionFlag.OccupiedInQuestEvent] || api.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || api.Condition[ConditionFlag.WatchingCutscene] || api.Condition[ConditionFlag.OccupiedSummoningBell])
            return "角色正忙，请结束当前操作";
        if (api.Condition[ConditionFlag.Mounted] || api.Condition[ConditionFlag.InFlight] || api.Condition[ConditionFlag.Swimming])
            return "首版仅支持地面步行或奔跑";
        return null;
    }
    public void Drive(MovementPosition position, float stopDistance)
    {
        if (!enabled || BlockReason() != null) { Stop(); return; }
        target = position.Vector; tolerance = stopDistance; lastDrive = Stopwatch.GetTimestamp();
    }
    public void Face(float radians)
    {
        if (!enabled || BlockReason() != null || api.ObjectTable.LocalPlayer is not { } player) return;
        ((GameObject*)player.Address)->SetRotation(MathF.IEEERemainder(radians, MathF.Tau));
    }
    public void Stop() => target = null;
    private void Detour(void* self, float* left, float* forward, float* turn, byte* strafe, byte* unknown, byte additive)
    {
        hook!.Original(self, left, forward, turn, strafe, unknown, additive);
        if (additive != 0 || !enabled) return;
        try
        {
            if (Math.Abs(*left) > 0.01f || Math.Abs(*forward) > 0.01f || Math.Abs(*turn) > 0.01f
                || api.KeyState[VirtualKey.ESCAPE])
            {
                target = null; manualUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency; return;
            }
            if (target is not { } destination) return;
            if (Stopwatch.GetElapsedTime(lastDrive).TotalSeconds > 0.25 || BlockReason() != null
                || !allowedOne!(self) || !allowedTwo!(self)) return;
            if (api.ObjectTable.LocalPlayer is not { } player) return;
            var diff = destination - player.Position;
            var distance = new Vector2(diff.X, diff.Z).Length();
            if (distance <= tolerance) return;
            var reference = player.Rotation;
            if (api.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1)
            {
                var manager = CameraManager.Instance();
                if (manager == null) return;
                var camera = manager->GetActiveCamera();
                if (camera == null) return;
                reference = camera->DirH + MathF.PI;
            }
            var direction = MathF.Atan2(diff.X, diff.Z) - reference;
            // Analog input eases into a station without changing the player's walking setting.
            var strength = Math.Clamp((distance - tolerance) * 1.5f, 0.15f, 1f);
            *left = MathF.Sin(direction) * strength; *forward = MathF.Cos(direction) * strength;
        }
        catch (Exception ex)
        {
            target = null; fault = "移动接口异常，已停止";
            api.PluginLog.Error(ex, "Movement input failed");
        }
    }
    public void Dispose() { Disable(); hook?.Dispose(); hook = null; disposed = true; }
}
