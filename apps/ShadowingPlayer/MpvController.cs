using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ShadowingPlayer
{
    public sealed class MpvController
    {
        private const string SentenceModeScriptName = "shadowing_sentence_mode";

        // FIX 3: QueryWriteTimeoutMilliseconds and PipeConnectTimeoutMilliseconds are no
        // longer needed for GetTimePositionAsync because we no longer open a second pipe.
        // Kept here only for the write timeout used in SendCommandAsync.
        private const int QueryTimeoutMilliseconds = 2000;

        private readonly Process process;
        private readonly NamedPipeClientStream pipe;
        private readonly StreamWriter writer;
        // FIX 3: reader for the single shared InOut pipe
        private readonly StreamReader reader;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
        // FIX 3: tracks in-flight get_property calls so the reader loop can route responses
        private readonly Dictionary<int, TaskCompletionSource<object>> pending
            = new Dictionary<int, TaskCompletionSource<object>>();
        private readonly object pendingLock = new object();
        private int nextRequestId = 0;
        private readonly CancellationTokenSource readerCts = new CancellationTokenSource();
        private bool broken;

        private MpvController(Process process, NamedPipeClientStream pipe)
        {
            this.process = process;
            this.pipe = pipe;
            // FIX 2: AutoFlush = false so WriteLineAsync and FlushAsync do not run
            // concurrently. We flush explicitly inside the lock in SendCommandAsync.
            writer = new StreamWriter(pipe) { AutoFlush = false };
            // FIX 3: one reader on the same pipe instead of a second pipe connection
            reader = new StreamReader(pipe);
        }

        public static async Task<MpvController> StartAsync(IntPtr windowHandle)
        {
            var pipeName = "shadowing-player-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
            var pipePath = @"\\.\pipe\" + pipeName;
            var mpvPath = Environment.GetEnvironmentVariable("SHADOWING_MPV_PATH");

            if (string.IsNullOrWhiteSpace(mpvPath))
            {
                mpvPath = "mpv.exe";
            }

            var sentenceModeScriptPath = ResolveAppPath("shadowing_sentence_mode.lua");
            if (!File.Exists(sentenceModeScriptPath))
            {
                throw new InvalidOperationException("Sentence mode script not found: " + sentenceModeScriptPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = mpvPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments =
                    "--idle=yes " +
                    "--force-window=yes " +
                    "--terminal=no " +
                    "--no-config " +
                    "--script=" + QuoteArgument(sentenceModeScriptPath) + " " +
                    "--wid=" + windowHandle + " " +
                    "--input-ipc-server=\"" + pipePath + "\"",
            };

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "Windows could not find mpv.exe. Install mpv for Windows, add mpv.exe to PATH, or set SHADOWING_MPV_PATH to the full mpv.exe path.",
                    ex);
            }

            if (process == null)
            {
                throw new InvalidOperationException("mpv.exe could not be started.");
            }

            // FIX 3: InOut instead of Out so the same pipe can both send commands
            // and receive responses, eliminating the need for a second pipe connection.
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await ConnectWithTimeoutAsync(pipe, 5000);

            var controller = new MpvController(process, pipe);
            controller.StartReaderLoop();
            return controller;
        }

        public bool IsRunning
        {
            get { return !process.HasExited; }
        }

        public Task LoadFileAsync(string path)
        {
            return SendCommandAsync("loadfile", path, "replace");
        }

        public Task TogglePauseAsync()
        {
            return SendCommandAsync("cycle", "pause");
        }

        public Task SeekAsync(int seconds)
        {
            return SendCommandAsync("seek", seconds, "relative");
        }

        public Task SeekAbsoluteAsync(double seconds)
        {
            return SendCommandAsync("seek", seconds, "absolute", "exact");
        }

        public Task StopAsync()
        {
            return SendCommandAsync("stop");
        }

        public Task LoadSentenceSegmentsAsync(string json)
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "load", json);
        }

        public Task EnableSentenceModeAsync()
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "enable");
        }

        public Task DisableSentenceModeAsync()
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "disable");
        }

        public Task PlaySentenceAsync(int oneBasedIndex)
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "play", oneBasedIndex);
        }

        public Task RepeatSentenceAsync()
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "repeat");
        }

        public Task PreviousSentenceAsync()
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "previous");
        }

        public Task NextSentenceAsync()
        {
            return SendCommandAsync("script-message-to", SentenceModeScriptName, "next");
        }

        public Task SetPauseAsync(bool paused)
        {
            return SendCommandAsync("set_property", "pause", paused);
        }

        public Task SetSpeedAsync(double speed)
        {
            return SendCommandAsync("set_property", "speed", speed);
        }

        public Task AddSubtitleAsync(string path)
        {
            return SendCommandAsync("sub-add", path, "cached", "Generated transcript");
        }

        public Task SetSubtitleVisibilityAsync(bool visible)
        {
            return SendCommandAsync("set_property", "sub-visibility", visible);
        }

        // GetTimePositionAsync is retained for callers that need raw playback time
        // (e.g. enabling sentence mode at the current position). It now goes through
        // the single shared pipe via QueryAsync instead of opening a second connection.
        public async Task<double?> GetTimePositionAsync()
        {
            try
            {
                var data = await QueryAsync(QueryTimeoutMilliseconds, "get_property", "time-pos");
                if (data == null) return null;
                return Convert.ToDouble(data, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        // Returns the current sentence index (0-based) as last written by the Lua script
        // into user-data/shadow/index. This is the preferred way to sync the transcript
        // highlight after a sentence change, because Lua writes the value synchronously
        // when it moves to a new sentence — no polling, no second pipe, no timing race.
        // Returns null if mpv is not playing or sentence mode is not active.
        public async Task<int?> GetCurrentSentenceIndexAsync()
        {
            try
            {
                var data = await QueryAsync(QueryTimeoutMilliseconds, "get_property", "user-data/shadow/index");
                if (data == null) return null;
                return Convert.ToInt32(data, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        // Sends a command that expects a response and waits for it via the reader loop.
        private async Task<object> QueryAsync(int timeoutMs, params object[] command)
        {
            if (broken)
                throw new InvalidOperationException("mpv IPC is not available. Reopen the video to restart mpv.");

            int id;
            TaskCompletionSource<object> tcs;

            lock (pendingLock)
            {
                id = ++nextRequestId;
                tcs = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                pending[id] = tcs;
            }

            var payload = serializer.Serialize(new Dictionary<string, object>
            {
                { "command", command },
                { "request_id", id },
            });

            await writeLock.WaitAsync();
            try
            {
                if (broken)
                {
                    lock (pendingLock) pending.Remove(id);
                    throw new InvalidOperationException("mpv IPC is not available. Reopen the video to restart mpv.");
                }

                await writer.WriteLineAsync(payload);
                // FIX 2: explicit flush inside the lock; AutoFlush is off so only one
                // async operation runs on the StreamWriter at a time.
                await writer.FlushAsync();
            }
            catch
            {
                broken = true;
                lock (pendingLock) pending.Remove(id);
                throw;
            }
            finally
            {
                writeLock.Release();
            }

            var timeoutTask = Task.Delay(timeoutMs);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                lock (pendingLock) pending.Remove(id);
                return null;
            }

            return await tcs.Task;
        }

        // FIX 3: background loop that reads all responses from the shared pipe and
        // routes them to the matching QueryAsync caller by request_id.
        private void StartReaderLoop()
        {
            Task.Run(async () =>
            {
                try
                {
                    while (!readerCts.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break; // pipe closed by mpv
                        HandleIncomingLine(line);
                    }
                }
                catch
                {
                    // Pipe died. Mark broken immediately so that any pending QueryAsync
                    // calls fail fast rather than waiting out their full timeout silently.
                    broken = true;
                    DrainPendingQueries();
                }
            }, readerCts.Token);
        }

        private void HandleIncomingLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            try
            {
                var response = serializer.Deserialize<Dictionary<string, object>>(line);

                object rawId;
                if (!response.TryGetValue("request_id", out rawId)) return;

                var id = Convert.ToInt32(rawId, CultureInfo.InvariantCulture);

                TaskCompletionSource<object> tcs;
                lock (pendingLock)
                {
                    if (!pending.TryGetValue(id, out tcs)) return;
                    pending.Remove(id);
                }

                object data;
                response.TryGetValue("data", out data);
                tcs.TrySetResult(data);
            }
            catch
            {
                // Malformed JSON from mpv — ignore and keep reading.
            }
        }

        // Cancels every in-flight QueryAsync when the reader loop dies.
        // Without this, callers would silently wait until their individual timeouts expire.
        private void DrainPendingQueries()
        {
            List<TaskCompletionSource<object>> snapshot;
            lock (pendingLock)
            {
                snapshot = new List<TaskCompletionSource<object>>(pending.Values);
                pending.Clear();
            }

            foreach (var tcs in snapshot)
            {
                tcs.TrySetException(new InvalidOperationException(
                    "mpv IPC reader loop stopped. Reopen the video to restart mpv."));
            }
        }

        private async Task SendCommandAsync(params object[] command)
        {
            if (broken)
            {
                throw new InvalidOperationException("mpv IPC is not available. Reopen the video to restart mpv.");
            }

            var payload = serializer.Serialize(new Dictionary<string, object>
            {
                { "command", command },
            });

            await writeLock.WaitAsync();

            try
            {
                if (broken)
                {
                    throw new InvalidOperationException("mpv IPC is not available. Reopen the video to restart mpv.");
                }

                await writer.WriteLineAsync(payload);
                await writer.FlushAsync();
            }
            catch
            {
                broken = true;
                throw;
            }
            finally
            {
                writeLock.Release();
            }
        }

        private static async Task ConnectWithTimeoutAsync(NamedPipeClientStream pipe, int timeoutMilliseconds)
        {
            var connectTask = pipe.ConnectAsync(timeoutMilliseconds);
            var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMilliseconds));
            if (completed != connectTask)
            {
                ObserveFault(connectTask);
                throw new TimeoutException("Timed out connecting to mpv.");
            }

            await connectTask;
        }

        private static async void ObserveFault(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }

        private static string ResolveAppPath(string relativePath)
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var pathFromBuildOutput = Path.GetFullPath(Path.Combine(basePath, @"..\..", relativePath));

            if (File.Exists(pathFromBuildOutput))
            {
                return pathFromBuildOutput;
            }

            return Path.GetFullPath(Path.Combine(basePath, relativePath));
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public async Task DisposeAsync()
        {
            // Stop the reader loop before anything else so it does not race
            // with disposal of the pipe and reader.
            readerCts.Cancel();

            // Only attempt a clean quit if the pipe is not already broken.
            if (!broken)
            {
                try
                {
                    await SendCommandAsync("quit");
                }
                catch
                {
                    // The player may already be closed.
                }
            }

            writer.Dispose();
            reader.Dispose();
            readerCts.Dispose();
            writeLock.Dispose();
            pipe.Dispose();

            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    await Task.Run(new Action(() => process.WaitForExit(1000)));
                }
            }
            catch
            {
            }

            process.Dispose();
        }
    }
}
