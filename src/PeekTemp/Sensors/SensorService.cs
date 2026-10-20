using System.Diagnostics;

namespace PeekTemp.Sensors;

/// <summary>Fixed-size ring buffer of samples; NaN marks a missing value.</summary>
internal sealed class History
{
    private readonly float[] _data;
    private int _next;
    private int _count;

    public History(int capacity)
    {
        _data = new float[capacity];
        Array.Fill(_data, float.NaN);
    }

    public int Capacity => _data.Length;

    public void Add(float? value)
    {
        lock (_data)
        {
            _data[_next] = value ?? float.NaN;
            _next = (_next + 1) % _data.Length;
            _count = Math.Min(_count + 1, _data.Length);
        }
    }

    /// <summary>Copies samples oldest → newest into <paramref name="target"/>, right-aligned; returns the count.</summary>
    public int CopyTo(float[] target)
    {
        lock (_data)
        {
            Array.Fill(target, float.NaN);
            int n = Math.Min(_count, target.Length);
            for (int i = 0; i < n; i++)
            {
                int src = (_next - n + i + _data.Length) % _data.Length;
                target[target.Length - n + i] = _data[src];
            }
            return n;
        }
    }

    public float Max()
    {
        lock (_data)
        {
            float max = float.NaN;
            foreach (var v in _data)
                if (!float.IsNaN(v) && (float.IsNaN(max) || v > max))
                    max = v;
            return max;
        }
    }
}

/// <summary>Polls the sensor sources on a background thread and publishes immutable snapshots.</summary>
internal sealed class SensorService : IDisposable
{
    public const int HistoryLength = 120;

    private readonly Action _onUpdated;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly LhmSource _lhm = new();
    private volatile bool _stop;
    private volatile SensorSnapshot _current = SensorSnapshot.Empty;
    private volatile IReadOnlyList<string> _gpuNames = Array.Empty<string>();
    private volatile bool _pawnIoInstalled;
    private DateTime _pawnIoCheckedAt;

    private volatile int _intervalMs = 1000;
    private volatile string? _preferredGpu;
    private bool? _hadCpuTemp;

    public History CpuTemp { get; } = new(HistoryLength);
    public History CpuLoad { get; } = new(HistoryLength);
    public History GpuTemp { get; } = new(HistoryLength);
    public History GpuLoad { get; } = new(HistoryLength);

    public SensorService(Action onUpdated)
    {
        _onUpdated = onUpdated;
        _thread = new Thread(Run) { IsBackground = true, Name = "PeekTemp sensors", Priority = ThreadPriority.BelowNormal };
    }

    public SensorSnapshot Current => _current;

    public IReadOnlyList<string> GpuNames => _gpuNames;

    public bool PawnIoInstalled => _pawnIoInstalled;

    public int IntervalMs
    {
        get => _intervalMs;
        set { _intervalMs = Math.Clamp(value, 250, 10_000); _wake.Set(); }
    }

    public string? PreferredGpu
    {
        get => _preferredGpu;
        set { _preferredGpu = value; _wake.Set(); }
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        while (!_stop)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Log.Throttled("poll", "Sensor poll failed: " + ex);
            }

            if (_stop) break;
            try { _onUpdated(); }
            catch (Exception ex) { Log.Throttled("notify", "Sensor notify failed: " + ex.Message); }

            var wait = (int)Math.Max(50, _intervalMs - sw.ElapsedMilliseconds);
            _wake.WaitOne(wait);
        }
    }

    private void Poll()
    {
        if (DateTime.UtcNow - _pawnIoCheckedAt > TimeSpan.FromSeconds(30))
        {
            _pawnIoInstalled = LhmSource.IsPawnIoInstalled;
            _pawnIoCheckedAt = DateTime.UtcNow;
        }

        var frame = new SensorFrame();
        if (_lhm.Read(frame, _preferredGpu))
            frame.Sources.Add(_lhm.Name);
        _gpuNames = _lhm.GpuNames;

        // Log when CPU temperature appears or disappears: the most common support question.
        bool hasCpuTemp = frame.CpuTemp != null;
        if (_hadCpuTemp != hasCpuTemp && frame.CpuLoad != null)
        {
            _hadCpuTemp = hasCpuTemp;
            Log.Info(hasCpuTemp
                ? $"CPU temperature available: {frame.CpuTemp:0.0} °C ({frame.CpuName})"
                : $"CPU temperature unavailable (elevated={Elevation.IsElevated}, PawnIO={_pawnIoInstalled})");
        }

        _current = new SensorSnapshot(
            frame.CpuTemp, frame.CpuLoad, frame.CpuPower, frame.CpuName,
            frame.GpuTemp, frame.GpuLoad, frame.GpuPower, frame.GpuMemUsedMb, frame.GpuMemTotalMb, frame.GpuName,
            string.Join(" + ", frame.Sources),
            frame.CpuTemp == null ? DiagnoseCpuTemp() : null);

        CpuTemp.Add(frame.CpuTemp);
        CpuLoad.Add(frame.CpuLoad);
        GpuTemp.Add(frame.GpuTemp);
        GpuLoad.Add(frame.GpuLoad);
    }

    private CpuTempIssue DiagnoseCpuTemp()
    {
        if (!_pawnIoInstalled) return CpuTempIssue.NoDriver;
        if (!Elevation.IsElevated) return CpuTempIssue.NotElevated;
        return CpuTempIssue.SensorNotFound;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(3000))
            Log.Info("Sensor thread did not stop in time");
        _lhm.Dispose();
        _wake.Dispose();
    }
}
