# Optional device audio

`Strands.Agents.Bidi.Audio` supplies microphone capture, speaker playback, transcript/tool console output and optional managed voice processing. Core and Bidi have no dependency on this package.

```csharp
await using var audio = new BidiAudioIO(model, new BidiAudioOptions
{
    Processing = new BidiAudioProcessingOptions()
});
await agent.RunAsync(new[] { audio.Input }, new[] { audio.Output },
    cancellationToken: cancellationToken);
```

Pass the same `IAudioBidiModel` capability used by the agent. Construction does not access devices. Run starts devices after connection, snapshots resolved input/output formats, and physically awaits stream closure on Stop, caller cancellation or failure. The model, tool host and optional `ConsoleOutput` are borrowed. Console writes and cleanup are awaited; the helper never disposes the borrowed console sink. Device indices default to the host defaults and can be selected explicitly.

Formats are mono little-endian signed PCM16 (`pcm`), at 8000–192000 Hz. Unsupported layouts/encodings fail before device startup. Input and output rates may differ: the echo reference uses streaming linear resampling, including 16 kHz capture with 24 kHz playback. Barge-in clears queued playback and its echo reference. Playback underflow emits silence. Capture buffers are detached and bounded; overflow drops the oldest capture chunk to keep live input current. Playback never silently discards queued speech: exceeding `OutputBufferBytes` throws an `IOException` atomically. Increase this limit when providers produce long responses faster than the device can play them. This explicit failure avoids blocking the ordered event pump ahead of a barge-in event.

`Processing` is opt-in. Its independent operations are normalized adaptive echo cancellation, spectral noise reduction and peak-limited gain control. Noise estimation uses a local spectral floor and does not require an initial silence period. `StreamDelayMilliseconds` is a 0–1000 ms speaker-to-microphone delay hint and requires echo cancellation. Disabling all operations preserves PCM bits exactly. Noise processing introduces a short analysis-window delay. This is a managed voice helper, with measured offline quality tests; it makes no claim of WebRTC/Speex equivalence or hard real-time scheduling.

Native PortAudio runtime packages supply Linux x64/arm64 and macOS x64/arm64 assets. Pinned genuine Windows builds supply x86 and x64 assets with static CRT, so clean Framework 4.8 machines do not require a VC runtime installation. Packing fails if either Windows asset is absent. The shared `netstandard2.0` API supports Framework 4.8 consumers, while `net8.0` and `net10.0` have ordinary modern assets. Framework package targets copy both Windows native architectures for the process-specific loader. TFM compatibility alone does not establish native platform execution.

Build both Windows architectures with existing MSVC/CMake/Ninja and Windows SDK tools:

```powershell
./native/Build-PortAudioWindows.ps1 -Architecture x86 -WorkingDirectory C:/temp/portaudio-x86 -OutputDirectory C:/temp/portaudio-assets
./native/Build-PortAudioWindows.ps1 -Architecture x64 -WorkingDirectory C:/temp/portaudio-x64 -OutputDirectory C:/temp/portaudio-assets
# Supply /p:PortAudioNativeAssetsPath=C:/temp/portaudio-assets when building/packing.
```

The build verifies the pinned upstream archive and requested PE architecture and rejects imported VC/UCRT runtime DLLs, uses static CRT with WinMM/WASAPI, and does not open audio hardware. The caller owns cleanup of its dedicated build directory. PortAudio's license is included under `native-licenses`.

Linux loading requires the PortAudio asset's ALSA and JACK shared-library dependencies, even for the version-only check. On Ubuntu 22.04 install `libasound2` and `libjack-jackd2-0`; on newer Ubuntu releases use `libasound2t64` when that package supplies `libasound.so.2`. Both `libasound.so.2` and `libjack.so.0` must resolve. Installing these libraries does not enable microphone/speaker tests; device access remains opt-in.

Automated tests use deterministic device callbacks and offline PCM. Native-load/version checks do not initialize PortAudio or access hardware. Real microphone/speaker, latency, permissions and per-OS native execution require an explicit opt-in hardware check; they are separate from these automated results.
