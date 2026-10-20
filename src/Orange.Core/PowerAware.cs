using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orange.Core;

/// <summary>
/// Keeps conversions cool and quiet: fewer parallel jobs and threads, and Windows "efficiency mode" (EcoQoS) for
/// child processes when the laptop runs on battery. On mains power conversions use about half the cores so the
/// rest of the PC stays responsive.
/// </summary>
public static class PowerAware
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    /// <summary>Test hook; null asks Windows.</summary>
    public static bool? OnBatteryOverride { get; set; }

    public static bool OnBattery
    {
        get
        {
            if (OnBatteryOverride is { } o) return o;
            if (!OperatingSystem.IsWindows()) return false;
            try { return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0; } catch { return false; }
        }
    }

    public static int Cores => Math.Max(1, Environment.ProcessorCount);

    /// <summary>Files converted at the same time: 1 on battery, otherwise the user's setting.</summary>
    public static int ParallelJobs(int configured) => OnBattery ? 1 : Math.Max(1, configured);

    /// <summary>Threads one FFmpeg/ImageMagick job may use: about half the cores split across parallel jobs.</summary>
    public static int ThreadsPerJob(int parallel) => Math.Max(1, (OnBattery ? Cores / 4 : Cores / 2) / Math.Max(1, parallel));

    /// <summary>Efficiency mode for a child process (Windows 11 EcoQoS; ignored where unsupported).</summary>
    public static void Throttle(Process p)
    {
        if (!OperatingSystem.IsWindows() || !OnBattery) return;
        try
        {
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = 1, StateMask = 1 };   // EXECUTION_SPEED
            SetProcessInformation(p.Handle, 4 /*ProcessPowerThrottling*/, ref st, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
        }
        catch { }
    }
}
