using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Vigil.Recorder
{
    /// <summary>
    /// Process and machine numbers once a second: CPU use of the game and of the whole machine,
    /// working set, private memory, page faults (memory read back from disk stalls everything)
    /// and how full the machine's memory is. Windows through kernel32, Linux (the dedicated
    /// server) through /proc. Anything unavailable stays NaN.
    /// </summary>
    internal sealed class SystemStats
    {
        public double ProcessCpuPct = double.NaN, SystemCpuPct = double.NaN, WorkingSetMb = double.NaN,
            PrivateMb = double.NaN, PageFaultsPerSec = double.NaN, MemoryLoadPct = double.NaN, AvailableMb = double.NaN,
            Threads = double.NaN;

        private readonly bool _windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        private readonly int _cpus = Math.Max(1, Environment.ProcessorCount);
        private long _lastWall = Stopwatch.GetTimestamp();
        private ulong _lastProcCpu, _lastSysIdle, _lastSysTotal, _lastFaults;
        private bool _primed;

        /// <summary>Free space on the drive the game writes to (saves and backups need it), and on Linux the 1-minute load average.</summary>
        public double DiskFreeGb = double.NaN, LoadAverage = double.NaN;
        private string _diskPath;
        private int _diskCountdown;

        public void Sample()
        {
            long wall = Stopwatch.GetTimestamp();
            double seconds = (wall - _lastWall) / (double)Stopwatch.Frequency;
            _lastWall = wall;
            try { if (_windows) SampleWindows(seconds); else SampleLinux(seconds); }
            catch (Exception) { }
            _primed = true;
            if (--_diskCountdown <= 0)
            {
                _diskCountdown = 30; // free space changes slowly
                try
                {
                    if (_diskPath == null) _diskPath = Path.GetPathRoot(Path.GetFullPath(BepInEx.Paths.BepInExRootPath));
                    DiskFreeGb = new DriveInfo(_diskPath).AvailableFreeSpace / 1073741824.0;
                }
                catch (Exception) { }
            }
            if (!_windows)
            {
                try { LoadAverage = double.Parse(File.ReadAllText("/proc/loadavg").Split(' ')[0], CultureInfo.InvariantCulture); }
                catch (Exception) { }
            }
        }

        public string Describe()
        {
            return _windows ? "Windows (kernel32)" : "Linux (/proc)";
        }

        // ---- Windows ----

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr p, out FILETIME c, out FILETIME e, out FILETIME k, out FILETIME u);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
        [DllImport("kernel32.dll")] private static extern bool K32GetProcessMemoryInfo(IntPtr p, out PROCESS_MEMORY_COUNTERS_EX c, uint cb);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        private void SampleWindows(double seconds)
        {
            IntPtr me = GetCurrentProcess();
            if (GetProcessTimes(me, out _, out _, out var k, out var u))
            {
                ulong cpu = k.Value + u.Value; // 100 ns units
                if (_primed && seconds > 0) ProcessCpuPct = (cpu - _lastProcCpu) / 1e7 / seconds / _cpus * 100;
                _lastProcCpu = cpu;
            }
            if (GetSystemTimes(out var idle, out var kern, out var user))
            {
                ulong total = kern.Value + user.Value; // kernel time includes idle
                if (_primed && total > _lastSysTotal)
                    SystemCpuPct = 100.0 * (1 - (double)(idle.Value - _lastSysIdle) / (total - _lastSysTotal));
                _lastSysIdle = idle.Value; _lastSysTotal = total;
            }
            var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX)) };
            if (K32GetProcessMemoryInfo(me, out pmc, pmc.cb))
            {
                WorkingSetMb = pmc.WorkingSetSize.ToUInt64() / 1048576.0;
                PrivateMb = pmc.PrivateUsage.ToUInt64() / 1048576.0;
                if (_primed && seconds > 0) PageFaultsPerSec = (pmc.PageFaultCount - (uint)_lastFaults) / seconds;
                _lastFaults = pmc.PageFaultCount;
            }
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref ms))
            {
                MemoryLoadPct = ms.dwMemoryLoad;
                AvailableMb = ms.ullAvailPhys / 1048576.0;
            }
            try { Threads = Process.GetCurrentProcess().Threads.Count; } catch (Exception) { }
        }

        // ---- Linux ----

        private void SampleLinux(double seconds)
        {
            var ci = CultureInfo.InvariantCulture;
            // /proc/self/stat: fields after the ")" : state(3) ... minflt(10) cminflt majflt(12) cmajflt utime(14) stime(15) ... num_threads(20) ... rss(24)
            string stat = File.ReadAllText("/proc/self/stat");
            var f = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
            ulong utime = ulong.Parse(f[11], ci), stime = ulong.Parse(f[12], ci);
            ulong majflt = ulong.Parse(f[9], ci);
            Threads = double.Parse(f[17], ci);
            double hz = 100.0;
            ulong cpu = utime + stime;
            if (_primed && seconds > 0)
            {
                ProcessCpuPct = (cpu - _lastProcCpu) / hz / seconds / _cpus * 100;
                PageFaultsPerSec = (majflt - _lastFaults) / seconds; // major faults only: the ones that hit disk
            }
            _lastProcCpu = cpu; _lastFaults = majflt;

            foreach (var line in File.ReadAllLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:")) WorkingSetMb = Kb(line) / 1024.0;
                else if (line.StartsWith("RssAnon:")) PrivateMb = Kb(line) / 1024.0;
            }
            double total = double.NaN, avail = double.NaN;
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = Kb(line);
                else if (line.StartsWith("MemAvailable:")) avail = Kb(line);
            }
            if (!double.IsNaN(total) && !double.IsNaN(avail) && total > 0) { AvailableMb = avail / 1024.0; MemoryLoadPct = 100 * (1 - avail / total); }

            var cpuLine = File.ReadAllLines("/proc/stat")[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            ulong sum = 0; for (int i = 1; i < cpuLine.Length; i++) sum += ulong.Parse(cpuLine[i], ci);
            ulong idleAll = ulong.Parse(cpuLine[4], ci) + (cpuLine.Length > 5 ? ulong.Parse(cpuLine[5], ci) : 0);
            if (_primed && sum > _lastSysTotal) SystemCpuPct = 100.0 * (1 - (double)(idleAll - _lastSysIdle) / (sum - _lastSysTotal));
            _lastSysTotal = sum; _lastSysIdle = idleAll;
        }

        private static double Kb(string line)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? double.Parse(parts[1], CultureInfo.InvariantCulture) : double.NaN;
        }
    }
}
