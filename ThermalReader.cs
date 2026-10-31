using LibreHardwareMonitor.Hardware;

namespace ProjectOpti;

public static class ThermalReader
{
    /// <summary>CPU temps usually need administrator rights. Idle readings say little about throttling.</summary>
    public static (double? Cpu, double? Gpu) Read()
    {
        double? cpu = null, gpu = null;
        try
        {
            var pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            pc.Open();
            foreach (var hw in pc.Hardware)
            {
                hw.Update();
                bool isCpu = hw.HardwareType == HardwareType.Cpu;
                bool isGpu = hw.HardwareType.ToString().StartsWith("Gpu");
                foreach (var sensor in hw.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue) continue;
                    double v = sensor.Value.Value;
                    if (isCpu) cpu = Math.Max(cpu ?? 0, v);
                    if (isGpu) gpu = Math.Max(gpu ?? 0, v);
                }
            }
            pc.Close();
        }
        catch (Exception ex)
        {
            Logger.Log("Temperature read failed: " + ex.Message);
        }
        return (cpu, gpu);
    }
}
