using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BardStage.Core.Rooms;

public sealed record LocalEnsembleTarget(Guid Node, ulong Cid);
public sealed record LocalEnsembleSnapshot(Guid Node, int Process, double At, long Frequency,
    LargeContext Context, bool Enabled, Guid Group, bool Host, LocalEnsembleTarget[] Targets,
    LargeReport? Report, LargeEnvelope? Frame)
{
    public void ValidateCapacity()
    {
        if (Context?.Members is not { Length: <= LargePlan.MaxPlayers }
            || Targets is not { Length: <= LargePlan.MaxPlayers })
            throw new InvalidDataException("同机独立合奏最多 8 人，拒绝超过 8 人的本机参与组");
        Frame?.ValidateCapacity();
    }
}

/// <summary>One latest-state slot per process instance, isolated from the legacy MidiBard IPC bus.</summary>
[SupportedOSPlatform("windows")]
public sealed class LocalEnsembleBus : IDisposable
{
    private const int Slots = 64, SlotBytes = 64 * 1024, HeaderBytes = 32;
    private readonly MemoryMappedFile map = null!;
    private readonly MemoryMappedViewAccessor view = null!;
    private readonly Mutex mutex;
    private readonly int slot;
    private bool disposed;
    public Guid Node { get; } = Guid.NewGuid();
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static string DefaultName
    {
        get
        {
            var identity = Environment.UserDomainName + "/" + Environment.UserName + "/" + Process.GetCurrentProcess().SessionId;
            return "Local\\MidiBard.Large.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        }
    }
    public LocalEnsembleBus(string? name = null)
    {
        name ??= DefaultName;
        mutex = new Mutex(false, name + ".Lock");
        var locked = false;
        try
        {
            map = MemoryMappedFile.CreateOrOpen(name, Slots * SlotBytes, MemoryMappedFileAccess.ReadWrite);
            view = map.CreateViewAccessor();
            locked = Enter(1000);
            if (!locked) throw new IOException("本机同步忙，请稍后再试");
            slot = Enumerable.Range(0, Slots).FirstOrDefault(i => view.ReadInt32(i * SlotBytes) == 0
                || Now - view.ReadDouble(i * SlotBytes + 8) > 5, -1);
            if (slot < 0) throw new IOException("本机同步实例过多，请关闭未使用的插件实例");
            view.Write(slot * SlotBytes, -1);
            view.Write(slot * SlotBytes + 8, Now);
            view.WriteArray(slot * SlotBytes + 16, Node.ToByteArray(), 0, 16);
        }
        catch
        {
            if (locked) { mutex.ReleaseMutex(); locked = false; }
            DisposeResources(); mutex.Dispose(); throw;
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }
    private bool Enter(int timeout = 0)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }
    public bool Write(LocalEnsembleSnapshot snapshot)
    {
        if (disposed) return false;
        snapshot.ValidateCapacity();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (bytes.Length > SlotBytes - HeaderBytes) throw new InvalidDataException("本机合奏信息过大");
        if (!Enter()) return false;
        try
        {
            var id = new byte[16]; view.ReadArray(slot * SlotBytes + 16, id, 0, 16);
            if (new Guid(id) != Node) throw new IOException("本机同步会话已过期，请重新开启同机模式");
            view.WriteArray(slot * SlotBytes + HeaderBytes, bytes, 0, bytes.Length);
            view.Write(slot * SlotBytes + 8, snapshot.At);
            view.Write(slot * SlotBytes, bytes.Length);
            return true;
        }
        finally { mutex.ReleaseMutex(); }
    }
    public LocalEnsembleSnapshot[]? Read()
    {
        if (disposed || !Enter()) return null;
        var copies = new List<(byte[] Bytes, Guid Node, double At)>();
        try
        {
            for (var i = 0; i < Slots; i++)
            {
                var size = view.ReadInt32(i * SlotBytes);
                var at = view.ReadDouble(i * SlotBytes + 8);
                if (size is <= 0 or > SlotBytes - HeaderBytes || !double.IsFinite(at) || Now - at is < -.1 or > 1.5) continue;
                var bytes = new byte[size]; view.ReadArray(i * SlotBytes + HeaderBytes, bytes, 0, size);
                var id = new byte[16]; view.ReadArray(i * SlotBytes + 16, id, 0, 16);
                copies.Add((bytes, new Guid(id), at));
            }
        }
        finally { mutex.ReleaseMutex(); }
        // Parsing is deliberately outside the cross-process mutex.
        var result = new List<LocalEnsembleSnapshot>();
        foreach (var copy in copies)
        {
            try
            {
                var value = JsonSerializer.Deserialize<LocalEnsembleSnapshot>(copy.Bytes);
                value?.ValidateCapacity();
                if (value == null || value.Node == Guid.Empty || value.Node != copy.Node || value.At != copy.At
                    || value.Frequency != Stopwatch.Frequency || value.Context?.Members is not { Length: <= LargePlan.MaxPlayers }
                    || value.Context.Members.Any(m => m == null) || value.Targets is not { Length: <= LargePlan.MaxPlayers }
                    || value.Targets.Any(t => t == null || t.Node == Guid.Empty || t.Cid == 0)
                    || value.Targets.Select(t => t.Node).Distinct().Count() != value.Targets.Length
                    || value.Targets.Select(t => t.Cid).Distinct().Count() != value.Targets.Length) continue;
                if (value.Report is { } r && (r.Cid != value.Context.SelfCid || r.Session == Guid.Empty
                    || !double.IsFinite(r.Drift) || r.Issue is not { Length: <= 500 })) continue;
                if (value.Frame is { } f && (!Enum.IsDefined(f.Phase) || f.Reports is not { Length: <= LargePlan.MaxPlayers }
                    || f.Reports.Any(r => r == null) || f.LocalSessions?.Count > LargePlan.MaxPlayers)) continue;
                result.Add(value);
            }
            catch (JsonException) { }
            catch (InvalidDataException) { }
        }
        return result.ToArray();
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Enter(20))
        {
            try
            {
                var id = new byte[16]; view.ReadArray(slot * SlotBytes + 16, id, 0, 16);
                if (new Guid(id) == Node) view.Write(slot * SlotBytes, 0);
            }
            finally { mutex.ReleaseMutex(); }
        }
        DisposeResources(); mutex.Dispose();
    }
    private void DisposeResources() { view?.Dispose(); map?.Dispose(); }
}
