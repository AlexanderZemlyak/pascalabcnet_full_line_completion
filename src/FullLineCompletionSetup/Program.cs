using FullLineCompletionSetup;
using System.Runtime.InteropServices;
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

    await release.DownloadAndExtractAsync(asset, options.InstallDir, options.CancellationToken);
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
    var modelSource = options.ModelFile ?? options.ModelUrl ??
        Environment.GetEnvironmentVariable("PABC_FULLLINE_MODEL_URL");
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
        if (File.Exists(modelPath))
        {
            Console.WriteLine($"Model already exists: {modelPath}");
        }
        else if (options.ModelFile is not null)
        {
            Console.WriteLine($"Copying model {options.ModelFile} -> {modelPath}");
            File.Copy(options.ModelFile, modelPath);
        }
        else
        {
            Console.WriteLine($"Downloading model {modelSource}");
            using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
            using var response = await http.GetAsync(modelSource, HttpCompletionOption.ResponseHeadersRead,
                options.CancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(options.CancellationToken);
            await using var target = File.Create(modelPath);
            await source.CopyToAsync(target, options.CancellationToken);
        }
    }

    var config = new SetupConfig
    {
        SchemaVersion = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        Os = RuntimeInformation.OSDescription,
        Arch = RuntimeInformation.ProcessArchitecture.ToString(),
        DeviceClass = pickedClass.ToString(),
        DeviceName = device?.Name,
        LlamaTag = release.Tag,
        Asset = asset,
        InstallDir = Path.GetFullPath(options.InstallDir),
        ServerPath = Path.GetFullPath(serverPath),
        ModelPath = modelPath is null ? null : Path.GetFullPath(modelPath),
        ServerArgs = modelPath is null
            ? new[] { "--port", "8080" }
            : new[] { "--model", Path.GetFullPath(modelPath), "--port", "8080" }
    };
    var configPath = Path.Combine(options.InstallDir, "fullline-setup.json");
    await File.WriteAllTextAsync(configPath,
        JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }),
        options.CancellationToken);

    Console.WriteLine($"Done. Config written to {configPath}");
    Console.WriteLine($"Start the server with: \"{config.ServerPath}\" {string.Join(' ', config.ServerArgs)}");
    return 0;
}

static string? FindExecutable(string directory, string name)
{
    var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;
    return Directory
        .EnumerateFiles(directory, fileName, SearchOption.AllDirectories)
        .FirstOrDefault();
}

sealed class Options
{
    public string InstallDir { get; private set; } = DefaultInstallDir();
    public DeviceClass Device { get; private set; } = DeviceClass.Auto;
    public string? ModelUrl { get; private set; }
    public string? ModelFile { get; private set; }
    public string? ModelName { get; private set; }
    public bool NoModel { get; private set; }
    public bool DryRun { get; private set; }
    public bool Quiet { get; private set; }
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
                case "--no-model":
                    options.NoModel = true;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--quiet":
                    options.Quiet = true;
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
              --no-model          Skip the model step
              --dry-run           Only print what would be downloaded
              --quiet             Less output
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
    public string Os { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string LlamaTag { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string InstallDir { get; set; } = string.Empty;
    public string ServerPath { get; set; } = string.Empty;
    public string? ModelPath { get; set; }
    public string[] ServerArgs { get; set; } = Array.Empty<string>();
}
