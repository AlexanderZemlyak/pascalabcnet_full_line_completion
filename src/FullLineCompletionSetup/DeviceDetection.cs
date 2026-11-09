using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FullLineCompletionSetup;

/// <summary>
/// Target device для llama.cpp. Приоритет: дискретная GPU,
/// потом встроенная, потом процессор.
/// </summary>
public enum DeviceClass
{
    Auto,
    DiscreteGpu,
    IntegratedGpu,
    Cpu,
    Vulkan,
    Cuda,
    Metal
}

public sealed record DetectedDevice(
    DeviceClass Class,
    string Name,
    string Detail)
{
    public override string ToString() => $"{Class}: {Name} ({Detail})";
}

public static class DeviceDetection
{
    public static DeviceClass HostOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? DeviceClass.Metal : DeviceClass.Auto;

    public static IReadOnlyList<DetectedDevice> ListAdapters()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ListWindowsAdapters();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ListLinuxAdapters();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new[]
                {
                    new DetectedDevice(DeviceClass.DiscreteGpu, "Apple GPU", "Metal is always available on macOS")
                };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Device detection failed: {exception.Message}");
        }

        return Array.Empty<DetectedDevice>();
    }

    public static (DeviceClass Class, DetectedDevice? Device) PickAuto()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return (DeviceClass.Metal, ListAdapters()[0]);

        var adapters = ListAdapters();
        var discrete = adapters.FirstOrDefault(a => a.Class == DeviceClass.DiscreteGpu);
        if (discrete is not null)
            return (DeviceClass.DiscreteGpu, discrete);

        var integrated = adapters.FirstOrDefault(a => a.Class == DeviceClass.IntegratedGpu);
        if (integrated is not null)
            return (DeviceClass.IntegratedGpu, integrated);

        return (DeviceClass.Cpu, null);
    }

    private static IReadOnlyList<DetectedDevice> ListWindowsAdapters()
    {
        var names = RunCapture(
            "powershell.exe",
            "-NoProfile -Command \"Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name }\"");
        return names
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(Classify)
            .ToList();
    }

    private static IReadOnlyList<DetectedDevice> ListLinuxAdapters()
    {
        var lines = RunCapture("sh", "-c \"lspci 2>/dev/null | grep -iE 'vga|3d|display'\"");
        if (lines.Count > 0)
            return lines
                .Select(line => line[(line.IndexOf(':') + 1)..].Trim())
                .Select(Classify)
                .ToList();

        // No lspci: fall back to the presence of a DRM device node.
        return Directory.Exists("/dev/dri")
            ? new[] { new DetectedDevice(DeviceClass.IntegratedGpu, "DRM GPU", "/dev/dri present") }
            : Array.Empty<DetectedDevice>();
    }

    private static DetectedDevice Classify(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("geforce") || lower.Contains("quadro") || lower.Contains("rtx") ||
            lower.Contains("gtx") || lower.Contains("tesla") || lower.Contains("nvidia") ||
            lower.Contains("radeon rx") || lower.Contains("radeon pro") ||
            lower.Contains("instinct") || lower.Contains("firepro"))
        {
            return new DetectedDevice(DeviceClass.DiscreteGpu, name, "discrete GPU");
        }

        return new DetectedDevice(DeviceClass.IntegratedGpu, name, "integrated GPU");
    }

    private static IReadOnlyList<string> RunCapture(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        if (process is null)
            return Array.Empty<string>();

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }
}
