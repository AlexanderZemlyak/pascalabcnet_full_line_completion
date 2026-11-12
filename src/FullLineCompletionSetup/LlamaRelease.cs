using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FullLineCompletionSetup;

/// <summary>
/// Находит и скачивает llama.cpp release с официального GitHub репозитория ggml-org/llama.cpp
/// </summary>
public sealed class LlamaRelease
{
    public const string RepositoryApi = "https://api.github.com/repos/ggml-org/llama.cpp";

    private static readonly HttpClient Http = CreateClient();

    public string Tag { get; }
    public IReadOnlyList<string> Assets { get; }

    private LlamaRelease(string tag, IReadOnlyList<string> assets)
    {
        Tag = tag;
        Assets = assets;
    }

    public static async Task<LlamaRelease> ResolveLatestAsync(CancellationToken cancellationToken)
    {
        using var latest = await Http.GetAsync($"{RepositoryApi}/releases/latest", cancellationToken);
        latest.EnsureSuccessStatusCode();
        using var latestJson = JsonDocument.Parse(await latest.Content.ReadAsStringAsync(cancellationToken));
        var latestAssets = latestJson.RootElement.GetProperty("assets").EnumerateArray()
            .Select(asset => asset.GetProperty("name").GetString()!)
            .ToList();

        if (latestAssets.Any(name => name.EndsWith(".zip") || name.EndsWith(".tar.gz")))
        {
            var tag = latestJson.RootElement.GetProperty("tag_name").GetString()!;
            return new LlamaRelease(tag, latestAssets);
        }

        // Nightly scheme: the "latest" release only points at the real tag.
        var tagUrl = latestAssets.Contains("nightly-tag.txt")
            ? $"https://github.com/ggml-org/llama.cpp/releases/latest/download/nightly-tag.txt"
            : throw new InvalidOperationException("Cannot determine the llama.cpp nightly release tag.");
        var tag2 = (await Http.GetStringAsync(tagUrl, cancellationToken)).Trim();

        using var release = await Http.GetAsync($"{RepositoryApi}/releases/tags/{tag2}", cancellationToken);
        release.EnsureSuccessStatusCode();
        using var releaseJson = JsonDocument.Parse(await release.Content.ReadAsStringAsync(cancellationToken));
        var assets = releaseJson.RootElement.GetProperty("assets").EnumerateArray()
            .Select(asset => asset.GetProperty("name").GetString()!)
            .ToList();
        return new LlamaRelease(tag2, assets);
    }

    /// <summary>
    /// Выбирает ассет для запрошенного класса устройства по следующему правилу:
    /// GPU (дискретные или интегрированные) получают сборку Vulkan (работает для обоих),
    /// CPU переключается на сборку CPU, macOS всегда использует Metal, а CUDA
    /// подключается опционально для NVIDIA. Возвращает имя ассета и признак того,
    /// требуется ли также сопутствующий пакет среды выполнения CUDA (сборки CUDA для Windows).
    /// </summary>
    public (string Asset, string? CudaRuntimeAsset) PickAsset(DeviceClass device, bool warnOnFallback = true)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        string Require(Func<string, bool> predicate, string description)
        {
            var match = Assets.FirstOrDefault(name => name.StartsWith("llama-", StringComparison.Ordinal) && predicate(name));
            return match ?? throw new InvalidOperationException(
                $"No llama.cpp asset for {description} in release {Tag}. Available: {string.Join(", ", Assets)}");
        }

        if (isMac)
            return (Require(name => name == $"llama-{Tag}-bin-macos-{arch}.tar.gz", "macOS Metal"), null);

        var wantsCuda = device == DeviceClass.Cuda;
        var wantsCpu = device == DeviceClass.Cpu;

        if (wantsCuda)
        {
            if (isWindows && arch == "x64")
            {
                return (
                    Require(name => name.Contains("bin-win-cuda-12.4-x64"), "Windows CUDA 12.4"),
                    Assets.FirstOrDefault(name => name.StartsWith("cudart-", StringComparison.Ordinal) &&
                                                  name.Contains("cuda-12.4-x64")));
            }

            if (isLinux && arch == "x64")
                return (Require(name => name.Contains("bin-ubuntu-cuda-12.8-x64"), "Linux CUDA 12.8"), null);

            if (warnOnFallback)
                Console.Error.WriteLine("CUDA build is unavailable for this OS/architecture; falling back to Vulkan.");
        }

        if (!wantsCpu)
        {
            // Vulkan covers both discrete and integrated GPUs.
            if (isWindows)
                return (Require(name => name.EndsWith($"bin-win-vulkan-{arch}.zip"), $"Windows Vulkan {arch}"), null);
            if (isLinux)
                return (Require(name => name.EndsWith($"bin-ubuntu-vulkan-{arch}.tar.gz"), $"Linux Vulkan {arch}"), null);
        }

        if (isWindows)
            return (Require(name => name.EndsWith($"bin-win-cpu-{arch}.zip"), $"Windows CPU {arch}"), null);
        if (isLinux)
            return (Require(name => name == $"llama-{Tag}-bin-ubuntu-{arch}.tar.gz", $"Linux CPU {arch}"), null);

        throw new PlatformNotSupportedException($"Unsupported platform for llama.cpp prebuilt binaries.");
    }

    public string AssetUrl(string asset) =>
        $"https://github.com/ggml-org/llama.cpp/releases/download/{Tag}/{asset}";

    /// <summary>Скачивает ассет и распаковывает его в <paramref name="destination"/> (zip или tar.gz).</summary>
    /// <param name="progress">Прогресс скачивания (байт скачано, байт всего) для машинночитаемого вывода хоста.</param>
    public async Task DownloadAndExtractAsync(string asset, string destination, CancellationToken cancellationToken,
        Action<long, long?>? progress = null)
    {
        var url = AssetUrl(asset);
        var extension = asset.EndsWith(".tar.gz") ? ".tar.gz" : Path.GetExtension(asset);
        var archivePath = Path.Combine(Path.GetTempPath(), asset);

        Console.WriteLine($"Downloading {url}");
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(archivePath);
            var buffer = new byte[1 << 20];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                progress?.Invoke(copied, total);
                if (progress is null && total is > 0 && copied % (50 << 20) < buffer.Length)
                    Console.WriteLine($"  {copied / (1 << 20)} MB / {total / (1 << 20)} MB");
            }
        }

        Console.WriteLine($"Extracting {asset} into {destination}");
        Directory.CreateDirectory(destination);
        if (extension == ".tar.gz")
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
        }

        File.Delete(archivePath);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FullLineCompletionSetup", "1.0"));
        return client;
    }
}
