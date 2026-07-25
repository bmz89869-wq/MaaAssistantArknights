using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace BlueStacksStreamBridge;

internal static class Program
{
    private const string DeviceServerPath = "/data/local/tmp/scrcpy-server.jar";
    private const string ServerVersion = "4.1";
    private const uint H264CodecId = 0x68323634;
    private const ulong SessionFlag = 1UL << 63;
    private const ulong ConfigFlag = 1UL << 62;
    private const ulong KeyFrameFlag = 1UL << 61;
    private const ulong PtsMask = KeyFrameFlag - 1;

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        using var cancellation = new CancellationTokenSource();
        if (options.TimeoutSeconds > 0) cancellation.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        EventWaitHandle? stopEvent = null;
        RegisteredWaitHandle? stopRegistration = null;
        Process? parentProcess = null;
        EventHandler? parentExitedHandler = null;
        try
        {
            if (options.StopEventName is not null)
            {
                stopEvent = EventWaitHandle.OpenExisting(options.StopEventName);
                stopRegistration = ThreadPool.RegisterWaitForSingleObject(
                    stopEvent,
                    (_, _) => cancellation.Cancel(),
                    null,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
            }
            if (options.ParentPid is int parentPid)
            {
                parentProcess = Process.GetProcessById(parentPid);
                parentExitedHandler = (_, _) => cancellation.Cancel();
                parentProcess.Exited += parentExitedHandler;
                parentProcess.EnableRaisingEvents = true;
            }
            var result = options.SharedMemoryName is null
                ? await RunBenchmarkAsync(options, cancellation.Token)
                : await RunSharedMemoryAsync(options, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            if (options.JsonPath is not null)
            {
                var path = Path.GetFullPath(options.JsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
            }
            return 0;
        }
        catch (Exception ex)
        {
            var root = Unwrap(ex);
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                error = root.GetType().FullName,
                message = root.Message,
                hresult = $"0x{root.HResult:X8}",
            }, JsonOptions));
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            stopRegistration?.Unregister(null);
            stopEvent?.Dispose();
            if (parentProcess is not null && parentExitedHandler is not null)
                parentProcess.Exited -= parentExitedHandler;
            parentProcess?.Dispose();
        }
    }

    private static async Task<object> RunBenchmarkAsync(Options options, CancellationToken cancellationToken)
    {
        ValidateFiles(options);
        if (options.PushServer)
        {
            await RunAdbAsync(options, ["push", options.ServerPath, DeviceServerPath], TimeSpan.FromSeconds(20), cancellationToken);
        }

        var clock = await CalibrateClockAsync(options, cancellationToken);
        Stage("clock_calibrated", new { rttMs = clock.RttUs / 1000.0 });
        var port = ReserveTcpPort();
        var scid = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var socketName = $"scrcpy_{scid:x8}";
        Process? server = null;
        Process? ffmpeg = null;
        TcpClient? client = null;
        Task<string>? serverStdoutTask = null;
        Task<string>? serverStderrTask = null;
        Task<string>? ffmpegStderrTask = null;
        var forwardInstalled = false;
        var runStartedUs = HostUs();

        try
        {
            await RunAdbAsync(options, ["forward", $"tcp:{port}", $"localabstract:{socketName}"], TimeSpan.FromSeconds(10), cancellationToken);
            forwardInstalled = true;
            server = StartServer(options, scid);
            serverStdoutTask = server.StandardOutput.ReadToEndAsync();
            serverStderrTask = server.StandardError.ReadToEndAsync();
            Stage("server_started", new { scid = $"{scid:x8}", port });

            client = await ConnectScrcpyWithRetryAsync(port, cancellationToken);
            client.NoDelay = true;
            var stream = client.GetStream();
            Stage("socket_connected", new { port });

            var deviceNameBuffer = new byte[64];
            await stream.ReadExactlyAsync(deviceNameBuffer, cancellationToken);
            var nul = Array.IndexOf(deviceNameBuffer, (byte)0);
            var deviceName = Encoding.UTF8.GetString(deviceNameBuffer, 0, nul < 0 ? deviceNameBuffer.Length : nul);

            var fourBytes = new byte[4];
            await stream.ReadExactlyAsync(fourBytes, cancellationToken);
            var codecId = BinaryPrimitives.ReadUInt32BigEndian(fourBytes);
            if (codecId != H264CodecId) throw new InvalidDataException($"Expected H.264 codec id, got 0x{codecId:X8}.");

            var sessionHeader = new byte[12];
            await stream.ReadExactlyAsync(sessionHeader, cancellationToken);
            var session = ParseSession(sessionHeader);
            Stage("stream_header", new { deviceName, codec = "h264", session.Width, session.Height });
            ffmpeg = StartFfmpeg(options);
            ffmpegStderrTask = ffmpeg.StandardError.ReadToEndAsync();
            var metadata = Channel.CreateUnbounded<PacketMeta>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            var packetState = new PacketState();
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pumpTask = PumpPacketsAsync(
                stream,
                ffmpeg.StandardInput.BaseStream,
                metadata.Writer,
                packetState,
                session,
                allowSessionChanges: false,
                streamCts.Token);

            var frameSize = checked(session.Width * session.Height * 4);
            var frame = new byte[frameSize];
            var metrics = new List<FrameMetric>(options.Frames);
            var firstFrameUs = 0L;
            var totalFrames = options.WarmupFrames + options.Frames;
            for (var index = 0; index < totalFrames; index++)
            {
                await ffmpeg.StandardOutput.BaseStream.ReadExactlyAsync(frame, streamCts.Token);
                var decodedUs = HostUs();
                if (firstFrameUs == 0)
                {
                    firstFrameUs = decodedUs;
                    Stage("first_decoded_frame", new { elapsedMs = (decodedUs - runStartedUs) / 1000.0 });
                }
                var meta = await metadata.Reader.ReadAsync(streamCts.Token);
                var sourceHostUs = meta.PtsUs + clock.OffsetUs;
                if (index >= options.WarmupFrames)
                {
                    metrics.Add(new FrameMetric(
                        meta.PtsUs,
                        meta.ArrivalHostUs,
                        decodedUs,
                        meta.ArrivalHostUs - sourceHostUs,
                        decodedUs - sourceHostUs,
                        decodedUs - meta.ArrivalHostUs));
                }
            }

            streamCts.Cancel();
            client.Close();
            try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { ffmpeg.StandardInput.Close(); } catch { }
            if (!ffmpeg.WaitForExit(2000)) ffmpeg.Kill(entireProcessTree: true);
            if (!server.WaitForExit(2000)) server.Kill(entireProcessTree: true);

            var ffmpegErrText = await SafeTaskResult(ffmpegStderrTask);
            var serverErrText = await SafeTaskResult(serverStderrTask);
            _ = await SafeTaskResult(serverStdoutTask);
            var interFrameUs = metrics.Zip(metrics.Prepend(metrics[0]), (current, previous) => current.DecodedHostUs - previous.DecodedHostUs).Skip(1).ToList();
            var captureToArrival = metrics.Select(metric => metric.CaptureToArrivalUs).ToList();
            var endToEnd = metrics.Select(metric => metric.EndToEndUs).ToList();
            var receiveToDecode = metrics.Select(metric => metric.ReceiveToDecodeUs).ToList();

            return new
            {
                version = ServerVersion,
                options = new
                {
                    options.Serial,
                    options.MaxSize,
                    options.MaxFps,
                    options.BitRate,
                    options.Frames,
                    options.WarmupFrames,
                    options.Encoder,
                    options.CodecOptions,
                },
                device = new { name = deviceName, width = session.Width, height = session.Height, codec = "h264" },
                startup = new
                {
                    connectMs = Math.Round((firstFrameUs - runStartedUs) / 1000.0, 2),
                    clockSyncRttMs = Math.Round(clock.RttUs / 1000.0, 2),
                    clockOffsetMs = Math.Round(clock.OffsetUs / 1000.0, 2),
                },
                stream = new
                {
                    decodedFrames = metrics.Count,
                    mediaPackets = packetState.MediaPackets,
                    configPackets = packetState.ConfigPackets,
                    keyFrames = packetState.KeyFrames,
                    bytes = packetState.Bytes,
                    measuredFps = interFrameUs.Count == 0 ? 0 : Math.Round(1_000_000.0 / interFrameUs.Average(), 2),
                    frameIntervalMs = Summarize(interFrameUs),
                    captureToArrivalMs = Summarize(captureToArrival),
                    receiveToDecodeMs = Summarize(receiveToDecode),
                    endToEndMs = Summarize(endToEnd),
                    lastFrameHash16 = Convert.ToHexString(SHA256.HashData(frame)[..8]),
                },
                diagnostics = new
                {
                    ffmpeg = TrimDiagnostic(ffmpegErrText),
                    server = TrimDiagnostic(serverErrText),
                },
            };
        }
        catch (Exception ex)
        {
            client?.Close();
            KillIfRunning(ffmpeg);
            KillIfRunning(server);
            var serverOut = serverStdoutTask is null ? string.Empty : await SafeTaskResult(serverStdoutTask);
            var serverErr = serverStderrTask is null ? string.Empty : await SafeTaskResult(serverStderrTask);
            var ffmpegErr = ffmpegStderrTask is null ? string.Empty : await SafeTaskResult(ffmpegStderrTask);
            throw new InvalidOperationException(
                $"Stream bridge failed: {ex.Message}\nSERVER OUT:\n{TrimDiagnostic(serverOut)}\nSERVER ERR:\n{TrimDiagnostic(serverErr)}\nFFMPEG:\n{TrimDiagnostic(ffmpegErr)}",
                ex);
        }
        finally
        {
            client?.Dispose();
            KillIfRunning(ffmpeg);
            KillIfRunning(server);
            ffmpeg?.Dispose();
            server?.Dispose();
            if (forwardInstalled)
            {
                try { await RunAdbAsync(options, ["forward", "--remove", $"tcp:{port}"], TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
            }
        }
    }

    private static async Task<object> RunSharedMemoryAsync(Options options, CancellationToken cancellationToken)
    {
        ValidateFiles(options);
        if (options.PushServer)
        {
            await RunAdbAsync(options, ["push", options.ServerPath, DeviceServerPath], TimeSpan.FromSeconds(20), cancellationToken);
        }

        var port = ReserveTcpPort();
        var scid = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var socketName = $"scrcpy_{scid:x8}";
        Process? server = null;
        Process? ffmpeg = null;
        TcpClient? client = null;
        Task<string>? serverStdoutTask = null;
        Task<string>? serverStderrTask = null;
        Task<string>? ffmpegStderrTask = null;
        var forwardInstalled = false;
        var runStartedUs = HostUs();

        try
        {
            await RunAdbAsync(options, ["forward", $"tcp:{port}", $"localabstract:{socketName}"], TimeSpan.FromSeconds(10), cancellationToken);
            forwardInstalled = true;
            server = StartServer(options, scid);
            serverStdoutTask = server.StandardOutput.ReadToEndAsync();
            serverStderrTask = server.StandardError.ReadToEndAsync();
            Stage("server_started", new { scid = $"{scid:x8}", port, mode = "shared_memory" });

            client = await ConnectScrcpyWithRetryAsync(port, cancellationToken);
            client.NoDelay = true;
            var stream = client.GetStream();

            var deviceNameBuffer = new byte[64];
            await stream.ReadExactlyAsync(deviceNameBuffer, cancellationToken);
            var nul = Array.IndexOf(deviceNameBuffer, (byte)0);
            var deviceName = Encoding.UTF8.GetString(deviceNameBuffer, 0, nul < 0 ? deviceNameBuffer.Length : nul);

            var fourBytes = new byte[4];
            await stream.ReadExactlyAsync(fourBytes, cancellationToken);
            var codecId = BinaryPrimitives.ReadUInt32BigEndian(fourBytes);
            if (codecId != H264CodecId) throw new InvalidDataException($"Expected H.264 codec id, got 0x{codecId:X8}.");

            var sessionHeader = new byte[12];
            await stream.ReadExactlyAsync(sessionHeader, cancellationToken);
            var sourceSession = ParseSession(sessionHeader);
            var outputSession = new Session(options.OutputWidth, options.OutputHeight);
            var frameSize = checked(outputSession.Width * outputSession.Height * 4);
            using var latestFrame = new LatestFrameMapping(
                options.SharedMemoryName!,
                outputSession.Width,
                outputSession.Height,
                frameSize);
            Stage("shared_memory_ready", new
            {
                name = options.SharedMemoryName,
                deviceName,
                sourceWidth = sourceSession.Width,
                sourceHeight = sourceSession.Height,
                width = outputSession.Width,
                height = outputSession.Height,
                stride = outputSession.Width * 4,
                headerBytes = LatestFrameMapping.HeaderBytes,
                pixelFormat = "BGRA",
            });

            ffmpeg = StartFfmpeg(options, outputSession);
            ffmpegStderrTask = ffmpeg.StandardError.ReadToEndAsync();
            var packetState = new PacketState();
            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pumpTask = PumpPacketsAsync(
                stream,
                ffmpeg.StandardInput.BaseStream,
                null,
                packetState,
                sourceSession,
                allowSessionChanges: true,
                streamCts.Token);
            var frame = new byte[frameSize];
            var firstFrameUs = 0L;

            try
            {
                while (true)
                {
                    await ffmpeg.StandardOutput.BaseStream.ReadExactlyAsync(frame, streamCts.Token);
                    var decodedUs = HostUs();
                    latestFrame.Publish(frame, decodedUs);
                    if (firstFrameUs == 0)
                    {
                        firstFrameUs = decodedUs;
                        Stage("first_shared_frame", new { elapsedMs = (decodedUs - runStartedUs) / 1000.0 });
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }

            streamCts.Cancel();
            client.Close();
            try { await pumpTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { ffmpeg.StandardInput.Close(); } catch { }
            if (!ffmpeg.WaitForExit(2000)) ffmpeg.Kill(entireProcessTree: true);
            if (!server.WaitForExit(2000)) server.Kill(entireProcessTree: true);

            return new
            {
                mode = "shared-memory",
                device = new { name = deviceName, width = sourceSession.Width, height = sourceSession.Height, codec = "h264" },
                sharedMemory = new
                {
                    name = options.SharedMemoryName,
                    headerBytes = LatestFrameMapping.HeaderBytes,
                    width = outputSession.Width,
                    height = outputSession.Height,
                    frameBytes = frameSize,
                    publishedFrames = latestFrame.PublishedFrames,
                },
                stream = new
                {
                    mediaPackets = packetState.MediaPackets,
                    configPackets = packetState.ConfigPackets,
                    keyFrames = packetState.KeyFrames,
                    bytes = packetState.Bytes,
                },
            };
        }
        catch (Exception ex)
        {
            client?.Close();
            KillIfRunning(ffmpeg);
            KillIfRunning(server);
            var serverOut = serverStdoutTask is null ? string.Empty : await SafeTaskResult(serverStdoutTask);
            var serverErr = serverStderrTask is null ? string.Empty : await SafeTaskResult(serverStderrTask);
            var ffmpegErr = ffmpegStderrTask is null ? string.Empty : await SafeTaskResult(ffmpegStderrTask);
            throw new InvalidOperationException(
                $"Shared-memory stream bridge failed: {ex.Message}\nSERVER OUT:\n{TrimDiagnostic(serverOut)}\nSERVER ERR:\n{TrimDiagnostic(serverErr)}\nFFMPEG:\n{TrimDiagnostic(ffmpegErr)}",
                ex);
        }
        finally
        {
            client?.Dispose();
            KillIfRunning(ffmpeg);
            KillIfRunning(server);
            ffmpeg?.Dispose();
            server?.Dispose();
            if (forwardInstalled)
            {
                try { await RunAdbAsync(options, ["forward", "--remove", $"tcp:{port}"], TimeSpan.FromSeconds(5), CancellationToken.None); } catch { }
            }
        }
    }

    private static async Task PumpPacketsAsync(
        NetworkStream source,
        Stream decoderInput,
        ChannelWriter<PacketMeta>? metadata,
        PacketState state,
        Session initialSession,
        bool allowSessionChanges,
        CancellationToken cancellationToken)
    {
        var header = new byte[12];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await source.ReadExactlyAsync(header, cancellationToken);
                var ptsFlags = BinaryPrimitives.ReadUInt64BigEndian(header);
                if ((ptsFlags & SessionFlag) != 0)
                {
                    var changed = ParseSession(header);
                    if (!allowSessionChanges &&
                        (changed.Width != initialSession.Width || changed.Height != initialSession.Height))
                        throw new InvalidDataException($"Video size changed during benchmark: {initialSession.Width}x{initialSession.Height} to {changed.Width}x{changed.Height}.");
                    if (allowSessionChanges &&
                        (changed.Width != initialSession.Width || changed.Height != initialSession.Height))
                        Stage("source_size_changed", new { changed.Width, changed.Height });
                    continue;
                }

                var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
                if (size <= 0 || size > 32 * 1024 * 1024) throw new InvalidDataException($"Invalid H.264 packet size: {size}.");
                var packet = new byte[size];
                await source.ReadExactlyAsync(packet, cancellationToken);
                var arrivalUs = HostUs();
                var config = (ptsFlags & ConfigFlag) != 0;
                var key = (ptsFlags & KeyFrameFlag) != 0;
                if (config) state.ConfigPackets++;
                else
                {
                    state.MediaPackets++;
                    if (key) state.KeyFrames++;
                    if (state.MediaPackets == 1) Stage("first_media_packet", new { ptsUs = (long)(ptsFlags & PtsMask), size, key });
                    if (metadata is not null)
                        await metadata.WriteAsync(new PacketMeta((long)(ptsFlags & PtsMask), arrivalUs), cancellationToken);
                }
                state.Bytes += size;
                await decoderInput.WriteAsync(packet, cancellationToken);
                await decoderInput.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        finally
        {
            metadata?.TryComplete();
            try { decoderInput.Close(); } catch { }
        }
    }

    private static Session ParseSession(ReadOnlySpan<byte> header)
    {
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header);
        if ((flags & 0x80000000) == 0) throw new InvalidDataException("Expected a scrcpy video session header.");
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[4..]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]));
        if (width <= 0 || height <= 0 || width > 10000 || height > 10000) throw new InvalidDataException($"Invalid video size {width}x{height}.");
        return new Session(width, height);
    }

    private static Process StartServer(Options options, int scid)
    {
        var info = NewProcess(options.AdbPath);
        var arguments = new List<string>
        {
            "-s", options.Serial, "shell", $"CLASSPATH={DeviceServerPath}", "app_process", "/", "com.genymobile.scrcpy.Server", ServerVersion,
            $"scid={scid:x8}", "log_level=info", "tunnel_forward=true", "audio=false", "control=false", "cleanup=false",
            "power_on=false", "clipboard_autosync=false", $"max_size={options.MaxSize}", $"max_fps={options.MaxFps.ToString(CultureInfo.InvariantCulture)}",
            $"video_bit_rate={options.BitRate}", $"video_encoder={options.Encoder}",
        };
        if (!string.IsNullOrWhiteSpace(options.CodecOptions)) arguments.Add($"video_codec_options={options.CodecOptions}");
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start scrcpy-server.");
    }

    private static Process StartFfmpeg(Options options, Session? outputSession = null)
    {
        var info = NewProcess(options.FfmpegPath);
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "warning", "-flags", "low_delay", "-threads", "1", "-probesize", "32", "-analyzeduration", "0", "-fpsprobesize", "0",
            "-f", "h264", "-i", "pipe:0", "-an", "-vsync", "0", "-flush_packets", "1",
        }) info.ArgumentList.Add(arg);
        if (outputSession is not null)
        {
            info.ArgumentList.Add("-vf");
            info.ArgumentList.Add(
                $"scale={outputSession.Width}:{outputSession.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2," +
                $"pad={outputSession.Width}:{outputSession.Height}:(ow-iw)/2:(oh-ih)/2");
        }
        foreach (var arg in new[] { "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start FFmpeg.");
    }

    private static ProcessStartInfo NewProcess(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static async Task<ClockCalibration> CalibrateClockAsync(Options options, CancellationToken cancellationToken)
    {
        var info = NewProcess(options.AdbPath);
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add(options.Serial);
        info.ArgumentList.Add("shell");
        using var shell = Process.Start(info) ?? throw new InvalidOperationException("Could not start ADB shell for clock calibration.");
        var stderrTask = shell.StandardError.ReadToEndAsync(cancellationToken);
        var samples = new List<ClockCalibration>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var beforeUs = HostUs();
            await shell.StandardInput.WriteLineAsync("cat /proc/uptime");
            await shell.StandardInput.FlushAsync(cancellationToken);
            var line = await shell.StandardOutput.ReadLineAsync(cancellationToken);
            var afterUs = HostUs();
            if (line is null) break;
            var first = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var guestSeconds)) continue;
            var guestUs = checked((long)Math.Round(guestSeconds * 1_000_000));
            var rttUs = afterUs - beforeUs;
            var midpointUs = beforeUs + rttUs / 2;
            samples.Add(new ClockCalibration(midpointUs - guestUs, rttUs));
        }
        await shell.StandardInput.WriteLineAsync("exit");
        shell.StandardInput.Close();
        if (!shell.WaitForExit(2000)) shell.Kill(entireProcessTree: true);
        _ = await SafeTaskResult(stderrTask);
        return samples.OrderBy(sample => sample.RttUs).FirstOrDefault()
               ?? throw new InvalidOperationException("Could not calibrate guest monotonic clock over ADB shell.");
    }

    private static async Task<TcpClient> ConnectScrcpyWithRetryAsync(int port, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                client.NoDelay = true;
                var dummy = new byte[1];
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readDeadline.CancelAfter(500);
                await client.GetStream().ReadExactlyAsync(dummy, readDeadline.Token);
                if (dummy[0] != 0) throw new InvalidDataException($"Unexpected scrcpy dummy byte: {dummy[0]}.");
                return client;
            }
            catch (Exception ex) when ((ex is SocketException or IOException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                client.Dispose();
                await Task.Delay(50, cancellationToken);
            }
        }
        throw new IOException("Could not receive the scrcpy dummy byte through the forwarded socket.", last);
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> RunAdbAsync(Options options, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = NewProcess(options.AdbPath);
        foreach (var prefix in new[] { "-s", options.Serial }) info.ArgumentList.Add(prefix);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start ADB.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            KillIfRunning(process);
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"ADB exited with {process.ExitCode}: {error.Trim()}");
        return output;
    }

    private static object Summarize(IReadOnlyList<long> microseconds)
    {
        if (microseconds.Count == 0) return new { p50 = 0d, p95 = 0d, min = 0d, max = 0d };
        var sorted = microseconds.Order().ToArray();
        return new
        {
            p50 = Math.Round(Percentile(sorted, 0.50) / 1000.0, 2),
            p95 = Math.Round(Percentile(sorted, 0.95) / 1000.0, 2),
            min = Math.Round(sorted[0] / 1000.0, 2),
            max = Math.Round(sorted[^1] / 1000.0, 2),
        };
    }

    private static long Percentile(long[] sorted, double percentile) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    private static long HostUs()
    {
        var ticks = Stopwatch.GetTimestamp();
        return ticks / Stopwatch.Frequency * 1_000_000 + ticks % Stopwatch.Frequency * 1_000_000 / Stopwatch.Frequency;
    }

    private static void Stage(string name, object data) => Console.Error.WriteLine(JsonSerializer.Serialize(new { stage = name, data }));

    private static void ValidateFiles(Options options)
    {
        if (!File.Exists(options.AdbPath)) throw new FileNotFoundException("ADB not found.", options.AdbPath);
        if (!File.Exists(options.FfmpegPath)) throw new FileNotFoundException("FFmpeg not found.", options.FfmpegPath);
        if (options.PushServer && !File.Exists(options.ServerPath)) throw new FileNotFoundException("scrcpy-server not found.", options.ServerPath);
    }

    private static void KillIfRunning(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static async Task<string> SafeTaskResult(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch { return string.Empty; }
    }

    private static string? TrimDiagnostic(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length <= 2000 ? value : value[^2000..];
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex.InnerException is not null && ex is AggregateException or System.Reflection.TargetInvocationException) ex = ex.InnerException;
        return ex;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Session(int Width, int Height);
    private sealed record PacketMeta(long PtsUs, long ArrivalHostUs);
    private sealed record FrameMetric(long PtsUs, long ArrivalHostUs, long DecodedHostUs, long CaptureToArrivalUs, long EndToEndUs, long ReceiveToDecodeUs);
    private sealed record ClockCalibration(long OffsetUs, long RttUs);

    private sealed class PacketState
    {
        public int MediaPackets;
        public int ConfigPackets;
        public int KeyFrames;
        public long Bytes;
    }

    private unsafe sealed class LatestFrameMapping : IDisposable
    {
        // The map is intentionally sized for the largest supported profile so a restart at a
        // different stream resolution never changes the named mapping's ABI.
        public const int HeaderBytes = 128;
        private const int MaximumWidth = 1920;
        private const int MaximumHeight = 1080;
        private const int MaximumFrameBytes = MaximumWidth * MaximumHeight * 4;
        private const uint Magic = 0x4241414D; // "MAAB" in little-endian memory.
        private const uint Version = 1;
        private const uint Bgra = 0x41524742; // "BGRA" in little-endian memory.
        private const int SequenceOffset = 32;
        private const int PublishedFramesOffset = 40;
        private const int DecodedHostUsOffset = 48;
        private const int QpcFrequencyOffset = 56;

        private readonly MemoryMappedFile _mapping;
        private readonly MemoryMappedViewAccessor _view;
        private readonly int _frameBytes;
        private byte* _base;
        private long* _sequence;
        private long* _publishedFrames;
        private bool _disposed;

        public LatestFrameMapping(string name, int width, int height, int frameBytes)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Shared-memory name is required.", nameof(name));
            if (width <= 0 || height <= 0 || frameBytes <= 0 || frameBytes > MaximumFrameBytes)
                throw new ArgumentOutOfRangeException(nameof(frameBytes), "The shared-memory transport supports frames up to 1920x1080 BGRA.");

            _frameBytes = frameBytes;
            var mapBytes = HeaderBytes + (long)MaximumFrameBytes;
            _mapping = MemoryMappedFile.CreateOrOpen(name, mapBytes, MemoryMappedFileAccess.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, mapBytes, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _base = pointer + checked((int)_view.PointerOffset);
            _sequence = (long*)(_base + SequenceOffset);
            _publishedFrames = (long*)(_base + PublishedFramesOffset);
            Initialize(width, height);
        }

        public long PublishedFrames => Interlocked.Read(ref *_publishedFrames);

        public void Publish(ReadOnlySpan<byte> frame, long decodedHostUs)
        {
            if (frame.Length != _frameBytes) throw new ArgumentException("Unexpected decoded frame length.", nameof(frame));
            var writingSequence = Interlocked.Increment(ref *_sequence);
            if ((writingSequence & 1) == 0) throw new InvalidOperationException("Shared-memory sequence counter lost writer ownership.");

            frame.CopyTo(new Span<byte>(_base + HeaderBytes, _frameBytes));
            BinaryPrimitives.WriteInt64LittleEndian(Header[DecodedHostUsOffset..], decodedHostUs);
            Interlocked.Increment(ref *_publishedFrames);
            Thread.MemoryBarrier();
            var publishedSequence = Interlocked.Increment(ref *_sequence);
            if ((publishedSequence & 1) != 0) throw new InvalidOperationException("Shared-memory sequence counter did not publish an even value.");
        }

        private Span<byte> Header => new(_base, HeaderBytes);

        private void Initialize(int width, int height)
        {
            var header = Header;
            Interlocked.Increment(ref *_sequence);
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0);
            Thread.MemoryBarrier();
            header.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], HeaderBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header[12..], Bgra);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], width);
            BinaryPrimitives.WriteInt32LittleEndian(header[20..], height);
            BinaryPrimitives.WriteInt32LittleEndian(header[24..], checked(width * 4));
            BinaryPrimitives.WriteInt32LittleEndian(header[28..], _frameBytes);
            BinaryPrimitives.WriteInt64LittleEndian(header[QpcFrequencyOffset..], Stopwatch.Frequency);
            Thread.MemoryBarrier();
            Interlocked.Exchange(ref *_sequence, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mapping.Dispose();
        }
    }

    private sealed record Options(
        string AdbPath, string FfmpegPath, string ServerPath, string Serial, string Encoder, string CodecOptions,
        int MaxSize, float MaxFps, int BitRate, int OutputWidth, int OutputHeight, int Frames, int WarmupFrames,
        int TimeoutSeconds, bool PushServer, string? JsonPath,
        string? SharedMemoryName, string? StopEventName, int? ParentPid)
    {
        public static Options Parse(string[] args)
        {
            string Read(string key, string fallback)
            {
                var index = Array.FindIndex(args, item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
            }
            int ReadInt(string key, int fallback) => int.TryParse(Read(key, fallback.ToString(CultureInfo.InvariantCulture)), out var value) ? value : throw new ArgumentException($"Invalid {key}.");
            float ReadFloat(string key, float fallback) => float.TryParse(Read(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException($"Invalid {key}.");
            string? ReadOptional(string key)
            {
                var index = Array.FindIndex(args, item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }
            int? ReadOptionalInt(string key)
            {
                var text = ReadOptional(key);
                return text is null ? null : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new ArgumentException($"Invalid {key}.");
            }
            var sharedMemoryIndex = Array.FindIndex(args, item => item.Equals("--shared-memory", StringComparison.OrdinalIgnoreCase));
            var sharedMemoryName = sharedMemoryIndex < 0
                ? null
                : sharedMemoryIndex + 1 < args.Length && !args[sharedMemoryIndex + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[sharedMemoryIndex + 1]
                    : @"Local\MAA.BlueStacksStreamBridge.v1";

            var root = AppContext.BaseDirectory;
            var projectDirectory = Path.GetFullPath(Path.Combine(root, ".."));
            return new Options(
                Read("--adb", @"C:\Program Files\BlueStacks_nxt_cn\HD-Adb.exe"),
                Read("--ffmpeg", @"C:\Program Files\BlueStacks_nxt_cn\ffmpeg.exe"),
                Read("--server", Path.Combine(projectDirectory, "vendor", "scrcpy-server-v4.1")),
                Read("--serial", "127.0.0.1:5555"),
                Read("--encoder", "OMX.google.h264.encoder"),
                Read("--codec-options", string.Empty),
                ReadInt("--max-size", 1280),
                ReadFloat("--max-fps", 30),
                ReadInt("--bit-rate", 8_000_000),
                ReadInt("--output-width", 1280),
                ReadInt("--output-height", 720),
                ReadInt("--frames", 120),
                ReadInt("--warmup", 30),
                ReadInt("--timeout", sharedMemoryName is null ? 45 : 0),
                args.Contains("--push-server", StringComparer.OrdinalIgnoreCase),
                Array.FindIndex(args, item => item.Equals("--json", StringComparison.OrdinalIgnoreCase)) is var jsonIndex && jsonIndex >= 0 && jsonIndex + 1 < args.Length ? args[jsonIndex + 1] : null,
                sharedMemoryName,
                ReadOptional("--stop-event"),
                ReadOptionalInt("--parent-pid"));
        }
    }
}
