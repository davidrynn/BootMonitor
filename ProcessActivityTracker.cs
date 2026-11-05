using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BootMonitor
{
    /// <summary>
    /// Finds the processes doing the most work right now (CPU and I/O), across every process on the
    /// machine, not just startup entries. Things like updaters, scheduled tasks and services never appear
    /// in the Run keys or Startup folders, but they're often what keeps the PC busy after login.
    /// </summary>
    internal sealed class ProcessActivityTracker
    {
        private const float MinCpuPercent = 3f;   // ignore processes below this
        private const float MinIoMBps = 1f;

        private readonly record struct Snapshot(long CreationTime, long CpuTime, ulong IoBytes);

        private readonly Dictionary<int, Snapshot> _previous = new();
        private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = new();
        private readonly int _ownPid = Environment.ProcessId;

        /// <summary>Returns e.g. "Visual Studio Background Download (44 MB/s)  ·  Steam (12% CPU)", or null when nothing stands out.</summary>
        public string? Sample()
        {
            double elapsedSec = _clock.Elapsed.TotalSeconds;
            bool havePrevious = _clock.IsRunning && elapsedSec > 0.1;
            _clock.Restart();

            var current = new Dictionary<int, Snapshot>();
            var cpuByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var ioByName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var pathByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    int pid = p.Id;
                    if (pid == 0 || pid == _ownPid) continue; // System Idle Process / ourselves

                    IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero) continue; // protected process
                    try
                    {
                        if (!GetProcessTimes(h, out long created, out _, out long kernel, out long user)) continue;
                        GetProcessIoCounters(h, out IO_COUNTERS io);
                        var snap = new Snapshot(created, kernel + user, io.ReadTransferCount + io.WriteTransferCount);
                        current[pid] = snap;

                        if (!havePrevious || !_previous.TryGetValue(pid, out var prev) || prev.CreationTime != created)
                            continue;

                        string name = p.ProcessName;
                        double cpuPct = (snap.CpuTime - prev.CpuTime) / 1e7 / elapsedSec / Environment.ProcessorCount * 100.0;
                        double ioMBps = (snap.IoBytes - prev.IoBytes) / (1024.0 * 1024.0) / elapsedSec;
                        cpuByName[name] = cpuByName.GetValueOrDefault(name) + Math.Max(0, cpuPct);
                        ioByName[name] = ioByName.GetValueOrDefault(name) + Math.Max(0, ioMBps);
                        if (!pathByName.ContainsKey(name)) pathByName[name] = GetImagePath(h);
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }
            }

            _previous.Clear();
            foreach (var kv in current) _previous[kv.Key] = kv.Value;
            if (!havePrevious) return null;

            // Show the top I/O process and the top CPU process (one entry if they're the same program)
            var shown = new List<string>();
            string? topIo = ioByName.Where(kv => kv.Value >= MinIoMBps).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            string? topCpu = cpuByName.Where(kv => kv.Value >= MinCpuPercent).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
            if (topIo != null) shown.Add(topIo);
            if (topCpu != null && !shown.Contains(topCpu, StringComparer.OrdinalIgnoreCase)) shown.Add(topCpu);

            var parts = shown.Select(name =>
            {
                var stats = new List<string>();
                if (ioByName[name] >= MinIoMBps) stats.Add($"{ioByName[name]:0} MB/s");
                if (cpuByName[name] >= MinCpuPercent) stats.Add($"{cpuByName[name]:0}% CPU");
                return $"{DisplayName(name, pathByName)} ({string.Join(", ", stats)})";
            });
            return shown.Count == 0 ? null : string.Join("  ·  ", parts);
        }

        // Prefer the exe's file description (what Task Manager shows), e.g. "Visual Studio Background Download"
        private string DisplayName(string processName, Dictionary<string, string?> paths)
        {
            if (_displayNames.TryGetValue(processName, out var cached)) return cached;
            string display = processName;
            try
            {
                if (paths.GetValueOrDefault(processName) is string path && File.Exists(path))
                {
                    string? desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) display = desc.Trim();
                }
            }
            catch { }
            _displayNames[processName] = display;
            return display;
        }

        private static string? GetImagePath(IntPtr h)
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }

        // --- Win32 ---------------------------------------------------------------

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS counters);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
