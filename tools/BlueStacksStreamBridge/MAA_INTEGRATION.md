# MAA Latest-Frame Integration

This is the Stage 5 contract between `BlueStacksStreamBridge.exe` and MAA. It replaces only the screenshot acquisition path. Maatouch and all input/control traffic remain on MAA's existing path.

## Bridge lifecycle

MAA owns the bridge process. Start it when the selected connection is BlueStacks and the feature is enabled:

```text
BlueStacksStreamBridge.exe --shared-memory Local\MAA.BlueStacksStreamBridge.v1 --max-size 1280 --max-fps 30 --bit-rate 8000000
```

Use `--max-fps 60` only for consumers that need the newer frame more than they need lower encoder load. The bridge has no timeout in shared-memory mode; MAA stops its child when the connection closes or it switches back to normal capture.

MAA retries `OpenFileMappingW(FILE_MAP_READ, ...)` for up to 1.5 seconds after launching the child. After startup, a missing map, invalid header, stalled producer heartbeat, failed frame copy, or unavailable post-input frame falls back to the existing ADB screenshot path.

## ABI

`src/MaaCore/Controller/BlueStacksStreamBridge.*` is the consumer implementation and ABI source of truth. The named map is always `128 + 1920 * 1080 * 4` bytes. Only the first `frame_bytes` bytes after the 128-byte header contain pixels. The MAA child-process profile publishes a fixed `1280x720` frame, scaling and padding portrait source frames until BlueStacks returns to landscape.

The pixels are tightly packed BGRA rows. `decoded_host_us` contains the scrcpy capture PTS calibrated into the Windows performance-counter clock and converted to microseconds. It deliberately records when the device captured the frame, not when FFmpeg finished decoding it, so a buffered pre-input frame cannot satisfy the post-input watermark. `producer_heartbeat_us` is an aligned 64-bit value at header offset 64. The producer updates it about every 100 ms even when scrcpy emits no frame, and the consumer rejects it when it is invalid, uses a mismatched QPC frequency, or is more than one second old.

The producer is the only writer. It increments `sequence` to an odd value, overwrites the pixels and metadata, then increments it to an even value. The reader copies only when the value before and after the copy is equal and even. A transient collision is retried locally for at most 10 ms; the transport never returns a torn frame or queues stale frames.

## MAA call site

Keep one `LatestFrameReader` per MAA controller. Before the existing screenshot command:

1. If it is not open, call `open()`; do not create the mapping from MAA.
2. Call `try_copy_latest()`.
3. Reuse an unchanged decoded frame while `producer_heartbeat_us` is healthy. After any successful MAA input, require `decoded_host_us` to be newer than the input watermark and wait at most 250 ms before falling back to ADB.
4. Wrap the accepted `bgra` buffer in the image representation MAA already uses, converting BGRA only if that call site cannot accept four channels.
5. On any rejection, use the current ADB capture implementation and leave the bridge child running for the next request.

The bridge process is started near the end of a successful `BlueStacks` connection, not once per screenshot. MAA creates a unique mapping and stop-event name per controller. Successful ADB, Minitouch, and Maatouch input paths advance the same atomic frame watermark. Normal shutdown signals the event so the bridge removes its ADB forward; a Windows Job Object terminates descendants if the parent crashes. On a bridge exit, MAA closes the mapping and uses the existing fallback for the rest of that connection.
