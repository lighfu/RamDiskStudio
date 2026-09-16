using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using RamDisk.Core;

namespace RamDisk.App;

public sealed class EtwIoMonitor : IDisposable
{
    private TraceEventSession? session;
    private Task? processing;
    private volatile bool stopping;
    private volatile bool running;
    private volatile string? error;
    private long unresolvedEvents;
    public bool Running => running;
    public string? Error => error;
    public long UnresolvedEvents => Interlocked.Read(ref unresolvedEvents);
    public long EventsLost => session?.EventsLost ?? 0;
    public IoAccumulator Counter { get; } = new();

    public void Start()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var prefix = $"RamDiskStudio.FileIO.{identity.User?.Value}.";
        // Clean up only our user's abandoned sessions; never touch another profiler's kernel logger.
        foreach (var name in TraceEventSession.GetActiveSessionNames().Where(x => x.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!int.TryParse(name[prefix.Length..].Split('.')[0], out var pid)) continue;
            try { using var process = Process.GetProcessById(pid); continue; }
            catch (ArgumentException) { }
            using var orphan = new TraceEventSession(name, TraceEventSessionOptions.Attach);
            orphan.Stop();
        }
        session = new TraceEventSession(prefix + Environment.ProcessId + "." + Guid.NewGuid().ToString("N"))
        {
            StopOnDispose = true, BufferSizeMB = 16
        };
        // Accessing Source starts the session, so enable the kernel provider before obtaining it.
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIO |
            KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.DiskFileIO);
        var source = session.Source;
        void Record(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIOReadWriteTraceData data, bool write)
        {
            if ((data.IoFlags & 0x2) != 0) return;
            var path = data.FileName;
            if (string.IsNullOrEmpty(path)) { Interlocked.Increment(ref unresolvedEvents); return; }
            Counter.Record(path, unchecked((uint)data.IoSize), write, data.IoFlags);
        }
        source.Kernel.FileIORead += data => Record(data, false);
        source.Kernel.FileIOWrite += data => Record(data, true);
        running = true;
        processing = Task.Factory.StartNew(() =>
        {
            try { source.Process(); if (!stopping) error = "Windows の I/O イベント受信が終了しました。"; }
            catch (Exception ex) { if (!stopping) error = ex.Message; }
            finally { running = false; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Dispose()
    {
        stopping = true;
        session?.Dispose();
        processing?.GetAwaiter().GetResult();
        session = null;
        running = false;
    }
}
