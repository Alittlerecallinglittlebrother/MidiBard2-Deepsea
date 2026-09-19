using System;

namespace MidiBard.IPC;

public interface IIPCManager : IDisposable
{
    /// <summary>
    /// Broadcast a serialized message to all other instances.
    /// </summary>
    /// <param name="serialized">Proto-serialized + compressed payload</param>
    /// <param name="includeSelf">If true, the message is also dispatched locally</param>
    void BroadCast(byte[] serialized, bool includeSelf = false);
}
