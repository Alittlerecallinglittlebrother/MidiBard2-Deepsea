// Copyright (C) 2022 akira0245
// AGPL-3.0 – see LICENSE
//
// Original IPCManager by akira0245 (MidiBard project), adapted to IIPCManager.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;

using MidiBard.Util;

using TinyIpc.IO;
using TinyIpc.Messaging;

using static Dalamud.api;

namespace MidiBard.IPC;

/// <summary>
/// Windows implementation of IIPCManager using the original TinyIpc library.
/// Used on native Windows where Memory-Mapped Files and named primitives work correctly.
/// </summary>
internal class WindowsIPCManager : IIPCManager
{
    private readonly bool _initFailed;
    private bool _messagesQueueRunning = true;
    private readonly TinyMessageBus _messageBus;
    private readonly ConcurrentQueue<(byte[] serialized, bool includeSelf)> _messageQueue = new();
    private readonly AutoResetEvent _autoResetEvent = new(false);
    private readonly Dictionary<MessageTypeCode, Action<IPCEnvelope>> _methodInfos;

    internal WindowsIPCManager()
    {
        try
        {
            const long maxFileSize = 1 << 24;
            _messageBus = new TinyMessageBus(new TinyMemoryMappedFile("Midibard.IPC", maxFileSize), true);
            _messageBus.MessageReceived += MessageBus_MessageReceived;

            _methodInfos = typeof(IPCHandles)
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Select(i => (i.GetCustomAttribute<IPCHandleAttribute>()?.TypeCode, methodInfo: i))
                .Where(i => i.TypeCode != null)
                .ToDictionary(
                    i => (MessageTypeCode)i.TypeCode,
                    i => i.methodInfo.CreateDelegate<Action<IPCEnvelope>>(null));

            var thread = new Thread(() =>
            {
                PluginLog.Information("IPC message queue worker thread started (Windows/TinyIpc)");
                while (_messagesQueueRunning)
                {
                    PluginLog.Verbose("Try dequeue message");
                    while (_messageQueue.TryDequeue(out var dequeue))
                    {
                        try
                        {
                            var message = dequeue.serialized;
                            var messageLength = message.Length;
                            PluginLog.Verbose($"Dequeue serialized. length: {Dalamud.Utility.Util.FormatBytes(messageLength)}");

                            if (messageLength > maxFileSize)
                                throw new InvalidOperationException(
                                    $"Message size is too large! maxFileSize: {Dalamud.Utility.Util.FormatBytes(maxFileSize)}");

                            if (_messageBus.PublishAsync(new BinaryData(message)).Wait(5000))
                            {
                                PluginLog.Verbose("Message published.");
                                if (dequeue.includeSelf)
                                    MessageBus_MessageReceived(null, new TinyMessageReceivedEventArgs(new BinaryData(message)));
                            }
                            else
                            {
                                throw new TimeoutException("IPC did not publish within 5000 ms.");
                            }
                        }
                        catch (Exception e)
                        {
                            PluginLog.Warning(e, "Error when publishing IPC message");
                        }
                    }

                    _autoResetEvent.WaitOne();
                }

                PluginLog.Information("IPC message queue worker thread ended (Windows/TinyIpc)");
            });
            thread.IsBackground = true;
            thread.Start();
        }
        catch (PlatformNotSupportedException e)
        {
            PluginLog.Error(e, "TinyIpc init failed – platform not supported.");
            _initFailed = true;
        }
        catch (Exception e)
        {
            PluginLog.Error(e, "TinyIpc init failed.");
            _initFailed = true;
        }
    }

    private void MessageBus_MessageReceived(object sender, TinyMessageReceivedEventArgs e)
    {
        if (_initFailed) return;
        try
        {
            var sw = Stopwatch.StartNew();
            PluginLog.Verbose("Message received");
            var bytes = e.Message.ToArray().Decompress();
            PluginLog.Verbose($"Decompressed in {sw.Elapsed.TotalMilliseconds}ms");
            var message = bytes.ProtoDeserialize<IPCEnvelope>();
            PluginLog.Verbose($"Deserialized in {sw.Elapsed.TotalMilliseconds}ms");
            PluginLog.Debug(message.ToString());
            ProcessMessage(message);
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Error processing received IPC message");
        }
    }

    private void ProcessMessage(IPCEnvelope message)
    {
        if (!MidiBard.config.SyncClients) return;
        _methodInfos[message.MessageType](message);
    }

    public void BroadCast(byte[] serialized, bool includeSelf = false)
    {
        if (_initFailed) return;
        if (!MidiBard.config.SyncClients) return;
        try
        {
            PluginLog.Verbose($"Queuing message. length: {Dalamud.Utility.Util.FormatBytes(serialized.Length)}" +
                              (includeSelf ? " includeSelf" : null));
            _messageQueue.Enqueue((serialized, includeSelf));
            _autoResetEvent.Set();
        }
        catch (Exception e)
        {
            PluginLog.Warning(e, "Error queuing IPC message");
        }
    }

    private void ReleaseUnmanagedResources(bool disposing)
    {
        try
        {
            _messagesQueueRunning = false;
            if (_messageBus != null)
                _messageBus.MessageReceived -= MessageBus_MessageReceived;
            if (!_initFailed)
            {
                _autoResetEvent?.Set();
                _autoResetEvent?.Dispose();
                _messageBus?.Dispose();
            }
        }
        finally { }

        if (disposing)
            GC.SuppressFinalize(this);
    }

    public void Dispose() => ReleaseUnmanagedResources(true);

    ~WindowsIPCManager() => ReleaseUnmanagedResources(false);
}
