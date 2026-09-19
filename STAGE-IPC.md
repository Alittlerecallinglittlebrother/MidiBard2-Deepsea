# Stage playback IPC v1

These endpoints are added by this local integration. They do not exist in stock MidiBard2. Stage itself subscribes directly to Playback and does not consume these endpoints.

| Name | Dalamud subscriber type | Operation |
| --- | --- | --- |
| `MidiBard.Stage.PlaybackState.V1` | `ICallGateSubscriber<string>` | `InvokeFunc()` returns a JSON snapshot |
| `MidiBard.Stage.PlaybackStateChanged.V1` | `ICallGateSubscriber<string, object>` | Subscribe to JSON messages |

Payload example:

```json
{
  "protocol": 1,
  "state": {
    "playbackId": "00000000-0000-0000-0000-000000000001",
    "sequence": 1,
    "filePath": "D:\\MIDI\\song.mid",
    "kind": "Started",
    "atUtc": "2026-09-18T00:00:00+00:00"
  }
}
```

- `state` is `null` before the first observed event. This is a last-event snapshot, not a playback-position stream or a command API.
- `kind`: `Loaded`, `Started`, `Paused`, `Resumed`, `Finished`, `Stopped`.
- `Playback.Stopped` maps to `Paused`; MidiBard's explicit stop or replacement maps an active/paused playback to `Stopped`. Only `Playback.Finished` maps to `Finished`.
- `playbackId` stays constant through a pause/resume; a repeat after natural finish receives a new ID, even when the Playback object is reused.
- `sequence` increases for the lifetime of this observer. Consumers must reset sequence tracking when the provider unloads/reloads.
- `filePath` is a normalized absolute local path, or an empty string for remote streams without a local path. It is not a title or stable content hash.
- Events are queued from playback callbacks and published on Dalamud's framework update. `atUtc` is callback time, not publication time. No historical replay is provided; subscribe then read the snapshot and deduplicate by sequence.
- An observer disposal unregisters the snapshot function and removes playback event handlers. It does not invent a Finished event. Consumers must handle provider absence.
- Subscriber failures are logged without preventing later publications. Payloads contain local file paths and should not be forwarded externally by default.

Verification uses real DryWetMidi 7.2.0 without a MIDI output device, production controller/observer code, and DispatchProxy implementations of the actual Dalamud API 15 interfaces. The live Dalamud IPC bus and game ensemble timing still require in-game validation.
