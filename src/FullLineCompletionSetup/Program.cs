using FullLineCompletionSetup;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

// Устанавливает LLM runtime (llama.cpp) и модель 
// для плагина full line completion

var options = Options.Parse(args);
if (options is null)
    return 2;

try
{
    return await RunAsync(options);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Setup failed: {exception.Message}");
    return 1;
}

static async Task<int> RunAsync(Options options)
{
    var progress = new ProgressReporter(options.ProgressJson);

    var (pickedClass, device) = options.Device switch
    {
        DeviceClass.Auto => DeviceDetection.PickAuto(),
        DeviceClass.Cpu => (DeviceClass.Cpu, null),
        DeviceClass.Vulkan => (DeviceClass.Vulkan, null),
        DeviceClass.Cuda => (DeviceClass.Cuda, null),
        DeviceClass.Metal => (DeviceClass.Metal, null),
        _ => throw new ArgumentOutOfRangeException()
    };

    if (!options.Quiet)
    {
        Console.WriteLine("Detected display adapters:");
        foreach (var adapter in DeviceDetection.ListAdapters())
            Console.WriteLine($"  - {adapter}");
        Console.WriteLine($"Selected device class: {pickedClass}" +
            (device is null ? string.Empty : $" ({device.Name})"));
    }

    var release = await LlamaRelease.ResolveLatestAsync(options.CancellationToken);
    var (asset, cudaRuntimeAsset) = release.PickAsset(pickedClass == DeviceClass.Vulkan ? DeviceClass.Vulkan : pickedClass);
    if (!options.Quiet)
    {
        Console.WriteLine($"llama.cpp release: {release.Tag}");
        Console.WriteLine($"Selected asset:    {asset}");
        if (cudaRuntimeAsset is not null)
            Console.WriteLine($"CUDA runtime:      {cudaRuntimeAsset}");
    }

    if (options.DryRun)
    {
        Console.WriteLine("Dry run: nothing was downloaded.");
        return 0;
    }

    Directory.CreateDirectory(options.InstallDir);

    // Remove a previous extraction so upgrades do not mix builds.
    foreach (var stale in new[] { "llama-server", "llama-cli", "llama-server.exe", "llama-cli.exe" })
    {
        var path = Path.Combine(options.InstallDir, stale);
        if (File.Exists(path)) File.Delete(path);
    }

    progress.Stage("llama", $"Downloading llama.cpp {release.Tag}");
    await release.DownloadAndExtractAsync(asset, options.InstallDir, options.CancellationToken,
        (copied, total) => progress.ReportBytes("llama", copied, total));
    if (cudaRuntimeAsset is not null)
        await release.DownloadAndExtractAsync(cudaRuntimeAsset, options.InstallDir, options.CancellationToken);

    var serverPath = FindExecutable(options.InstallDir, "llama-server")
        ?? throw new FileNotFoundException("llama-server was not found after extraction.");

    if (!options.Quiet)
    {
        Console.WriteLine($"Verifying {serverPath} ...");
        var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (probe is not null)
        {
            probe.WaitForExit(15_000);
            if (probe.ExitCode != 0)
                Console.Error.WriteLine("Warning: llama-server --version returned a non-zero exit code.");
        }
    }

    string? modelPath = null;
    long? modelSizeBytes = null;
    string? modelSha256 = null;
    var modelUrl = options.ModelUrl ?? Environment.GetEnvironmentVariable("PABC_FULLLINE_MODEL_URL");
    var modelSource = options.ModelFile ?? modelUrl;

    if (options.NoModel)
    {
        if (!options.Quiet) Console.WriteLine("Model download skipped (--no-model).");
    }
    else if (modelSource is null)
    {
        Console.Error.WriteLine(
            "No model specified (use --model-url, --model-file or PABC_FULLLINE_MODEL_URL); " +
            "skipping the model download.");
    }
    else
    {
        var modelName = options.ModelName ?? Path.GetFileName(new Uri(modelSource, UriKind.RelativeOrAbsolute).IsAbsoluteUri
            ? new Uri(modelSource).AbsolutePath
            : modelSource);
        modelPath = Path.Combine(options.InstallDir, modelName);
        // Недокачанный файл с прошлого запуска не должен выглядеть как готовая модель.
        var partialPath = modelPath + ".partial";
        if (File.Exists(partialPath)) File.Delete(partialPath);

        if (File.Exists(modelPath))
        {
            Console.WriteLine($"Model already exists: {modelPath}");
            modelSizeBytes = new FileInfo(modelPath).Length;
            // Хэш не пересчитываем: он записан при первичной установке и
            // переносится из прежнего конфига ниже.
        }
        else if (options.ModelFile is not null)
        {
            Console.WriteLine($"Copying model {options.ModelFile} -> {modelPath}");
            (modelSizeBytes, modelSha256) = await CopyWithHashAsync(
                options.ModelFile, partialPath, "model", progress, options.CancellationToken);
            // Атомарно: битый файл не остаётся по итоговому пути.
            File.Move(partialPath, modelPath, overwrite: true);
        }
        else
        {
            Console.WriteLine($"Downloading model {modelSource}");
            using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            using var response = await http.GetAsync(modelSource, HttpCompletionOption.ResponseHeadersRead,
                options.CancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var source = await response.Content.ReadAsStreamAsync(options.CancellationToken))
            await using (var target = File.Create(partialPath))
            {
                using var sha = SHA256.Create();
                var buffer = new byte[1 << 20];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, options.CancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), options.CancellationToken);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    copied += read;
                    progress.ReportBytes("model", copied, total);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                modelSizeBytes = copied;
                modelSha256 = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }
            File.Move(partialPath, modelPath, overwrite: true);
        }
    }

    var configPath = Path.Combine(options.InstallDir, "fullline-setup.json");
    // Поля, которые мы сейчас не вычисляем (например, sha256 уже существующей
    // модели), переносим из прежнего конфига, чтобы паспорт установки не пустел.
    var previous = SetupConfig.TryRead(configPath);
    if (modelSha256 is null && previous?.ModelPath == modelPath)
    {
        modelSha256 = previous.ModelSha256;
        modelUrl ??= previous.ModelUrl;
        options.ModelRevision ??= previous.ModelRevision;
    }

    var config = new SetupConfig
    {
        SchemaVersion = 2,
        CreatedAt = DateTimeOffset.UtcNow,
        ToolVersion = typeof(Options).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        Os = RuntimeInformation.OSDescription,
        Arch = RuntimeInformation.ProcessArchitecture.ToString(),
        DeviceClass = pickedClass.ToString(),
        DeviceName = device?.Name,
        LlamaTag = release.Tag,
        Asset = asset,
        InstallDir = Path.GetFullPath(options.InstallDir),
        ServerPath = Path.GetFullPath(serverPath),
        ModelPath = modelPath is null ? null : Path.GetFullPath(modelPath),
        ModelUrl = modelUrl,
        ModelRevision = options.ModelRevision,
        ModelSizeBytes = modelSizeBytes,
        ModelSha256 = modelSha256,
        ServerArgs = modelPath is null
            ? new[] { "--port", "8080" }
            : new[] { "--model", Path.GetFullPath(modelPath), "--port", "8080" }
    };
    var configTmpPath = configPath + ".partial";
    await File.WriteAllTextAsync(configTmpPath,
        JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }),
        options.CancellationToken);
    File.Move(configTmpPath, configPath, overwrite: true);

    progress.Done(configPath);
    if (!options.Quiet)
    {
        Console.WriteLine($"Done. Config written to {configPath}");
        Console.WriteLine($"Start the server with: \"{config.ServerPath}\" {string.Join(' ', config.ServerArgs)}");
    }
    return 0;
}

static async Task<(long Size, string Sha256)> CopyWithHashAsync(string sourcePath, string targetPath,
    string stage, ProgressReporter progress, CancellationToken cancellationToken)
{
    await using var source = File.OpenRead(sourcePath);
    await using var target = File.Create(targetPath);
    using var sha = SHA256.Create();
    var buffer = new byte[1 << 20];
    long copied = 0;
    int read;
    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
    {
        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        sha.TransformBlock(buffer, 0, read, null, 0);
        copied += read;
        progress.ReportBytes(stage, copied, source.Length);
    }
    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    return (copied, Convert.ToHexString(sha.Hash!).ToLowerInvariant());
}

static string? FindExecutable(string directory, string name)
{
    var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;
    return Directory
        .EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
        .FirstOrDefault();
}

/// <summary>
/// Машиночитаемый прогресс для хостов: при --progress json пишет
/// NDJSON-события в stdout, остальной вывод не трогает. Без флага выводит
/// человекочитаемые строки.
/// </summary>
sealed class ProgressReporter
{
    private readonly bool json;

    public ProgressReporter(bool json) => this.json = json;

    public void Stage(string stage, string message)
    {
        if (json)
            Write(new Dictionary<string, object?> { ["event"] = "stage", ["stage"] = stage, ["message"] = message });
        else
            Console.WriteLine(message);
    }

    public void ReportBytes(string stage, long received, long? total)
    {
        if (json)
        {
            Write(new Dictionary<string, object?>
            {
                ["event"] = "progress", ["stage"] = stage, ["received"] = received, ["total"] = total
            });
            return;
        }
        if (total is > 0 && received % (50 << 20) < (1 << 20))
            Console.WriteLine($"  {received / (1 << 20)} MB / {total / (1 << 20)} MB");
    }

    public void Done(string configPath)
    {
        if (json)
            Write(new Dictionary<string, object?> { ["event"] = "done", ["config"] = configPath });
    }

    private static void Write(Dictionary<string, object?> payload) =>
        Console.WriteLine(JsonSerializer.Serialize(payload));
}

sealed class Options
{
    public string InstallDir { get; private set; } = DefaultInstallDir();
    public DeviceClass Device { get; private set; } = DeviceClass.Auto;
    public string? ModelUrl { get; private set; }
    public string? ModelFile { get; private set; }
    public string? ModelName { get; private set; }
    public string? ModelRevision { get; set; }
    public bool NoModel { get; private set; }
    public bool DryRun { get; private set; }
    public bool Quiet { get; private set; }
    public bool ProgressJson { get; private set; }
    public CancellationToken CancellationToken { get; private set; }

    public static Options? Parse(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dir" when index + 1 < args.Length:
                    options.InstallDir = args[++index];
                    break;
                case "--device" when index + 1 < args.Length:
                    options.Device = Enum.Parse<DeviceClass>(args[++index], ignoreCase: true);
                    break;
                case "--model-url" when index + 1 < args.Length:
                    options.ModelUrl = args[++index];
                    break;
                case "--model-file" when index + 1 < args.Length:
                    options.ModelFile = args[++index];
                    break;
                case "--model-name" when index + 1 < args.Length:
                    options.ModelName = args[++index];
                    break;
                case "--model-revision" when index + 1 < args.Length:
                    options.ModelRevision = args[++index];
                    break;
                case "--no-model":
                    options.NoModel = true;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--quiet":
                    options.Quiet = true;
                    break;
                case "--progress" when index + 1 < args.Length && args[index + 1] == "json":
                    index++;
                    options.ProgressJson = true;
                    break;
                case "--help" or "-h":
                    PrintHelp();
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[index]}");
                    PrintHelp();
                    return null;
            }
        }

        options.CancellationToken = new CancellationTokenSource(TimeSpan.FromHours(3)).Token;
        return options;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            FullLineCompletionSetup - installs llama.cpp and the completion model.

            Usage:
              dotnet FullLineCompletionSetup.dll [options]

            Options:
              --dir <path>        Install directory (default: %LOCALAPPDATA%\pascalabcnet\llama.cpp
                                  or ~/.local/share/pascalabcnet/llama.cpp)
              --device <class>    auto | discretegpu | integratedgpu | cpu | vulkan | cuda | metal
                                  (auto: discrete GPU > integrated GPU > CPU; GPUs use the
                                  Vulkan build, macOS uses Metal)
              --model-url <url>   Download the model from this URL into the install directory
              --model-file <path> Copy a local model file into the install directory
              --model-name <name> File name for the downloaded/copied model
              --model-revision <rev> Model revision (e.g. Hugging Face commit); recorded into
                                  fullline-setup.json for version checks
              --no-model          Skip the model step
              --dry-run           Only print what would be downloaded
              --quiet             Less output
              --progress json     Emit NDJSON progress events in stdout for host UIs:
                                  {"event":"stage"|"progress"|"done", ...}
            """);
    }

    private static string DefaultInstallDir() =>
        Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PascalABC.NET", 
                "llama.cpp"
            );
}

sealed class SetupConfig
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ToolVersion { get; set; }
    public string Os { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string LlamaTag { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string InstallDir { get; set; } = string.Empty;
    public string ServerPath { get; set; } = string.Empty;
    public string? ModelPath { get; set; }
    public string? ModelUrl { get; set; }
    public string? ModelRevision { get; set; }
    public long? ModelSizeBytes { get; set; }
    public string? ModelSha256 { get; set; }
    public string[] ServerArgs { get; set; } = Array.Empty<string>();

    public static SetupConfig? TryRead(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return null;
            return JsonSerializer.Deserialize<SetupConfig>(File.ReadAllText(configPath));
        }
        catch
        {
            return null;
        }
    }
}
