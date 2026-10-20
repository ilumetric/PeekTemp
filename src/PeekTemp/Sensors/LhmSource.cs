using LibreHardwareMonitor.Hardware;

namespace PeekTemp.Sensors;

/// <summary>
/// LibreHardwareMonitor. CPU/GPU load and GPU temperature work without admin rights;
/// CPU temperature needs admin rights and the PawnIO driver.
/// </summary>
internal sealed class LhmSource : ISensorSource
{
    private static readonly string[] CpuTempNames =
    {
        "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "CCDs Max (Tdie)", "Core Max", "Core Average",
    };

    private Computer? _computer;
    private bool _failed;

    public string Name => "LibreHardwareMonitor";

    public IReadOnlyList<string> GpuNames { get; private set; } = Array.Empty<string>();

    public static bool IsPawnIoInstalled
    {
        get
        {
            try { return LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled; }
            catch { return false; }
        }
    }

    public bool Read(SensorFrame frame, string? preferredGpu)
    {
        if (!EnsureOpen()) return false;
        var computer = _computer!;

        var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu != null)
        {
            Update(cpu);
            var sensors = AllSensors(cpu).ToList();
            SensorFrame.Fill(ref frame.CpuName, cpu.Name);
            SensorFrame.Fill(ref frame.CpuTemp, FindCpuTemp(sensors));
            SensorFrame.Fill(ref frame.CpuLoad, Find(sensors, SensorType.Load, "CPU Total"));
            SensorFrame.Fill(ref frame.CpuPower, Find(sensors, SensorType.Power, "Package", "CPU Package"));
        }

        var gpu = PickGpu(computer, preferredGpu);
        if (gpu != null)
        {
            Update(gpu);
            var sensors = AllSensors(gpu).ToList();
            SensorFrame.Fill(ref frame.GpuName, gpu.Name);
            SensorFrame.Fill(ref frame.GpuTemp,
                Find(sensors, SensorType.Temperature, "GPU Core") ?? First(sensors, SensorType.Temperature));
            SensorFrame.Fill(ref frame.GpuLoad,
                Find(sensors, SensorType.Load, "GPU Core", "D3D 3D") ?? First(sensors, SensorType.Load));
            SensorFrame.Fill(ref frame.GpuPower,
                Find(sensors, SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? First(sensors, SensorType.Power));
            SensorFrame.Fill(ref frame.GpuMemUsedMb,
                Find(sensors, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"));
            SensorFrame.Fill(ref frame.GpuMemTotalMb,
                Find(sensors, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"));
        }

        return true;
    }

    private bool EnsureOpen()
    {
        if (_computer != null) return true;
        if (_failed) return false;
        try
        {
            var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            computer.Open();
            _computer = computer;
            GpuNames = computer.Hardware.Where(IsGpu).Select(h => h.Name).ToList();
            Log.Info("LHM hardware: " + string.Join(", ", computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name}")) +
                     $"; PawnIO installed: {IsPawnIoInstalled}");
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Log.Error("LibreHardwareMonitor failed to open", ex);
            return false;
        }
    }

    private static bool IsGpu(IHardware h) =>
        h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static IHardware? PickGpu(Computer computer, string? preferred)
    {
        var gpus = computer.Hardware.Where(IsGpu).ToList();
        if (gpus.Count == 0) return null;
        return gpus.FirstOrDefault(g => GpuPicker.Matches(g.Name, preferred))
               ?? gpus.OrderByDescending(g => GpuPicker.Score(g.Name, g.HardwareType.ToString())).First();
    }

    private static void Update(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
            sub.Update();
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hardware) =>
        hardware.Sensors.Concat(hardware.SubHardware.SelectMany(s => s.Sensors));

    private static float? FindCpuTemp(List<ISensor> sensors)
    {
        var temp = Find(sensors, SensorType.Temperature, CpuTempNames);
        if (temp != null) return temp;

        // Anything else that looks like a real temperature ("Distance to TjMax" is not one).
        return sensors
            .Where(s => s.SensorType == SensorType.Temperature && IsValid(s) &&
                        !s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value)
            .FirstOrDefault();
    }

    /// <summary>Without its driver LHM reports 0 instead of "no value" for CPU temperature and power.</summary>
    private static bool IsValid(ISensor s) => s.Value is { } v && !float.IsNaN(v) && s.SensorType switch
    {
        SensorType.Temperature => v > 0 && v < 150,
        SensorType.Power => v > 0,
        _ => true,
    };

    private static float? Find(List<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var sensor = sensors.FirstOrDefault(s =>
                s.SensorType == type && IsValid(s) && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (sensor != null) return sensor.Value;
        }
        return null;
    }

    private static float? First(List<ISensor> sensors, SensorType type) =>
        sensors.FirstOrDefault(s => s.SensorType == type && IsValid(s))?.Value;

    public void Dispose()
    {
        try { _computer?.Close(); }
        catch (Exception ex) { Log.Error("LHM close", ex); }
        _computer = null;
    }
}
