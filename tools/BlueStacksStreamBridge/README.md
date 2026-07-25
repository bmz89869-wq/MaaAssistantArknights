# BlueStacksStreamBridge

Headless latest-frame screenshot bridge for BlueStacks 5.20 using the official scrcpy v4.1 server and BlueStacks' bundled FFmpeg.

The bridge does not record video or screenshots. It measures packet PTS to decoded-frame latency in benchmark mode, or publishes only the newest decoded BGRA frame to a fixed shared-memory map for MAA. It removes its ADB forward on exit and leaves only the 716 KB server jar under `/data/local/tmp` for subsequent runs.

The MAA integration starts this process only for the `BlueStacks` connection profile. It publishes a fixed `1280x720` BGRA frame, follows source orientation changes, and falls back to the existing ADB screenshot implementation whenever the map is missing, stale, or inconsistent.

```powershell
dotnet run -c Release -- --max-size 1280 --max-fps 30 --bit-rate 8000000 --warmup 30 --frames 180
```

For MAA's latest-frame transport, use the persistent mode below. The process is intended to be launched and owned by MAA.

```powershell
dotnet run -c Release -- --shared-memory Local\MAA.BlueStacksStreamBridge.v1 --max-size 1280 --max-fps 30 --bit-rate 8000000 --push-server
```

Deploy the framework-dependent build into an MAA installation with .NET 8 available:

```powershell
.\tools\BlueStacksStreamBridge\Publish-For-Maa.ps1 -Destination 'C:\path\to\MAA\BlueStacksStreamBridge'
```

The destination must contain the published executable files and `scrcpy-server-v4.1`. See `MAA_INTEGRATION.md` and `src/MaaCore/Controller/BlueStacksStreamBridge.*` for the lifecycle, ABI, and fallback behavior.
