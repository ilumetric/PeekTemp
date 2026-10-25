using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeekTemp.Sensors;

/// <summary>One application (all processes with the same image name) and its share of the CPU or GPU.</summary>
internal sealed record AppUsage(string Name, string? Path, float Percent);

/// <summary>
/// Finds the applications that load the CPU and GPU the most, the way Task Manager counts them:
/// CPU from per-process kernel+user time, GPU from the "GPU Engine" performance counters.
/// Sampling is only done while someone looks at it (the flyout is open): the GPU counters are not free.
/// </summary>
internal sealed class ProcessMonitor : IDisposable
{
    public const int TopCount = 3;

    private readonly Dictionary<(int Pid, long Created), long> _cpuTimes = new();
    private readonly Dictionary<string, (string Name, string? Path)> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = new();
    private byte[] _buffer = Array.Empty<byte>(); // allocated on first use, released on Stop
    private IntPtr _query, _gpuCounter;
    private bool _baseline;
    private volatile bool _active;

    public IReadOnlyList<AppUsage> TopCpu { get; private set; } = Array.Empty<AppUsage>();
    public IReadOnlyList<AppUsage> TopGpu { get; private set; } = Array.Empty<AppUsage>();

    /// <summary>Set from the UI thread; the next <see cref="Sample"/> starts or stops sampling.</summary>
    public bool Active
    {
        get => _active;
        set => _active = value;
    }

    /// <summary>False until two samples have been taken since activation.</summary>
    public bool Measured { get; private set; }

    /// <summary>True right after the first (baseline) sample, when a quick second one is worth it.</summary>
    public bool WantsFollowUp => _active && _baseline;

    /// <summary>Called on the sensor thread.</summary>
    public void Sample()
    {
        if (!_active)
        {
            if (_clock.IsRunning) Stop();
            return;
        }

        bool first = !_clock.IsRunning;
        double elapsed100ns = _clock.Elapsed.Ticks;
        _clock.Restart();
        if (first) OpenGpuQuery();

        var processes = ReadProcesses();
        if (processes == null) return;
        var gpu = ReadGpu();

        _baseline = first;
        if (first) return;

        // CPU: share of all logical processors, summed over the processes of one application.
        var cpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double capacity = elapsed100ns * Environment.ProcessorCount;
        foreach (var p in processes)
        {
            if (p.Pid == 0 || capacity <= 0) continue; // System Idle Process
            var share = p.CpuDelta / capacity * 100;
            if (share <= 0) continue;
            cpu[p.Name] = cpu.GetValueOrDefault(p.Name) + share;
        }
        TopCpu = Top(cpu, processes);

        // GPU: per engine, sum the processes of an application; the busiest engine counts (as in Task Manager).
        var names = new Dictionary<int, string>(processes.Count);
        foreach (var p in processes) names[p.Pid] = p.Name;
        var perEngine = new Dictionary<(string App, string Engine), double>();
        foreach (var (pid, engine, value) in gpu)
            if (value > 0 && names.TryGetValue(pid, out var name))
                perEngine[(name, engine)] = perEngine.GetValueOrDefault((name, engine)) + value;
        var gpuApps = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((app, _), value) in perEngine)
            gpuApps[app] = Math.Max(gpuApps.GetValueOrDefault(app), value);
        TopGpu = Top(gpuApps, processes);
        Measured = true;
    }

    private IReadOnlyList<AppUsage> Top(Dictionary<string, double> usage, List<ProcessSample> processes)
    {
        var result = new List<AppUsage>(TopCount);
        foreach (var (image, percent) in usage.OrderByDescending(kv => kv.Value).Take(TopCount))
        {
            if (percent < 0.05) break;
            var (name, path) = Describe(image, processes);
            result.Add(new AppUsage(name, path, (float)Math.Min(percent, 100)));
        }
        return result;
    }

    /// <summary>Friendly name ("Google Chrome") and exe path of an application, cached by image name.</summary>
    private (string Name, string? Path) Describe(string image, List<ProcessSample> processes)
    {
        if (_apps.TryGetValue(image, out var known)) return known;

        string name = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image[..^4] : image;
        string? path = null;
        foreach (var p in processes)
        {
            if (!string.Equals(p.Name, image, StringComparison.OrdinalIgnoreCase)) continue;
            path = ImagePath(p.Pid);
            if (path != null) break;
        }
        if (path != null)
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(description)) name = description;
            }
            catch (Exception ex)
            {
                Log.Throttled("procinfo", "Version info failed: " + ex.Message);
            }
        }
        // Protected processes (System, Registry...) have no readable path and keep the image name.
        if (_apps.Count > 1024) _apps.Clear();
        _apps[image] = (name, path);
        return (name, path);
    }

    private static string? ImagePath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new char[1024];
            int size = sb.Length;
            return QueryFullProcessImageNameW(handle, 0, sb, ref size) ? new string(sb, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private struct ProcessSample
    {
        public int Pid;
        public string Name;
        public long CpuDelta; // a process started since the previous call counts all of its time
    }

    /// <summary>All processes with the CPU time spent since the previous call (one system call, no handles).</summary>
    private unsafe List<ProcessSample>? ReadProcesses()
    {
        int status, needed;
        while (true)
        {
            fixed (byte* p = _buffer)
                status = NtQuerySystemInformation(SystemProcessInformation, (IntPtr)p, _buffer.Length, out needed);
            if (status != STATUS_INFO_LENGTH_MISMATCH) break;
            _buffer = new byte[Math.Max(needed, _buffer.Length) + 64 * 1024];
        }
        if (status != 0)
        {
            Log.Throttled("procs", $"NtQuerySystemInformation failed: 0x{status:X8}");
            return null;
        }

        var result = new List<ProcessSample>(512);
        var seen = new HashSet<(int, long)>();
        fixed (byte* start = _buffer)
        {
            int offset = 0;
            while (true)
            {
                byte* e = start + offset;
                long created = *(long*)(e + 32);
                long time = *(long*)(e + 40) + *(long*)(e + 48); // user + kernel, 100 ns
                ushort nameBytes = *(ushort*)(e + 56);
                IntPtr nameBuffer = *(IntPtr*)(e + 64);
                int pid = (int)*(long*)(e + 80);

                string name = pid == 0 ? "Idle"
                    : nameBuffer == IntPtr.Zero || nameBytes == 0 ? (pid == 4 ? "System" : "pid " + pid)
                    : new string((char*)nameBuffer, 0, nameBytes / 2);

                var key = (pid, created);
                seen.Add(key);
                _cpuTimes.TryGetValue(key, out var previous);
                _cpuTimes[key] = time;
                result.Add(new ProcessSample { Pid = pid, Name = name, CpuDelta = time - previous });

                int next = *(int*)e;
                if (next == 0) break;
                offset += next;
            }
        }

        // Forget processes that have exited.
        if (_cpuTimes.Count > seen.Count)
            foreach (var key in _cpuTimes.Keys.Where(k => !seen.Contains(k)).ToList())
                _cpuTimes.Remove(key);
        return result;
    }

    private void OpenGpuQuery()
    {
        CloseGpuQuery();
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0)
        {
            _query = IntPtr.Zero;
            return;
        }
        int status = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter);
        if (status != 0)
        {
            Log.Throttled("gpuengine", $"GPU Engine counters unavailable: 0x{status:X8}");
            CloseGpuQuery();
        }
    }

    private void CloseGpuQuery()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _gpuCounter = IntPtr.Zero;
    }

    /// <summary>
    /// Utilization of every (process, engine) pair since the previous call. Instance names look like
    /// "pid_1234_luid_0x00000000_0x0000D1B2_phys_0_eng_3_engtype_3D".
    /// </summary>
    private unsafe List<(int Pid, string Engine, double Value)> ReadGpu()
    {
        var result = new List<(int, string, double)>();
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return result;

        int size = 0;
        int status = PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out int count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size <= 0) return result;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = PdhGetFormattedCounterArrayW(_gpuCounter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
            if (status != 0) return result;
            var items = (PDH_FMT_COUNTERVALUE_ITEM*)buffer;
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                if (item.CStatus > 1) continue; // only PDH_CSTATUS_VALID_DATA / NEW_DATA
                var instance = new string(item.Name);
                if (!instance.StartsWith("pid_", StringComparison.Ordinal)) continue;
                int sep = instance.IndexOf('_', 4);
                if (sep < 0 || !int.TryParse(instance.AsSpan(4, sep - 4), out int pid)) continue;
                int engType = instance.IndexOf("_engtype_", StringComparison.Ordinal);
                var engine = engType > sep ? instance[sep..engType] : instance[sep..];
                result.Add((pid, engine, item.Value));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private void Stop()
    {
        _clock.Reset();
        _cpuTimes.Clear();
        _baseline = false;
        Measured = false;
        CloseGpuQuery();
        // Nothing is kept while the flyout is closed: the widget stays a plain sensor monitor.
        _buffer = Array.Empty<byte>();
        _apps.Clear();
        TopCpu = Array.Empty<AppUsage>();
        TopGpu = Array.Empty<AppUsage>();
    }

    public void Dispose() => CloseGpuQuery();

    // Native
    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const int PDH_FMT_DOUBLE = 0x00000200;
    private const int PDH_FMT_NOCAP100 = 0x00008000;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public char* Name;
        public int CStatus;
        public double Value;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, int format, ref int bufferSize, out int itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);
}
