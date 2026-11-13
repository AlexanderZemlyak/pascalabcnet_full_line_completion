using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Threading;
using System.Threading.Tasks;

namespace FullLineCompletionLspPlugin
{
    /// <summary>
    /// Владеет процессом llama-server: поднимает его по fullline-setup.json,
    /// если внешний сервер не задан переменной PABC_LLM_URL, ждёт готовности
    /// модели и гасит процесс при завершении LSP-сервера. Состояние процесса
    /// пишется в llama-server-state.json рядом с установкой — хост
    /// читает файл, чтобы показывать прогресс загрузки модели и прибирать
    /// осиротевшие процессы.
    /// </summary>
    public sealed class LlamaServerManager : IDisposable
    {
        /// <summary>Внешний сервер llama.cpp: PABC_LLM_URL.</summary>
        public const string UrlEnvironmentVariable = "PABC_LLM_URL";

        /// <summary>Каталог установки с fullline-setup.json: PABC_FULLLINE_DIR.</summary>
        public const string DirEnvironmentVariable = "PABC_FULLLINE_DIR";

        public const string StateFileName = "llama-server-state.json";

        private const int HealthTimeoutMinutes = 15;

        private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        private readonly TaskCompletionSource<string> ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Process? process;
        private string? installDir;
        private EventHandler? processExitHandler;
        private SafeJobHandle? job;

        private LlamaServerManager()
        {
        }

        /// <summary>
        /// Базовый URL сервера моделей. Пустая строка — сервер недоступен
        /// (комплект не установлен или запуск не удался). Задача завершается,
        /// когда llama-server готов принимать запросы (модель загружена).
        /// </summary>
        public Task<string> Ready => ready.Task;

        public static LlamaServerManager StartNew()
        {
            var manager = new LlamaServerManager();
            manager.Start();
            return manager;
        }

        private void Start()
        {
            var externalUrl = Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(externalUrl))
            {
                Console.Error.WriteLine($"[fullline] using external llama.cpp server at {externalUrl}");
                ready.TrySetResult(externalUrl.TrimEnd('/'));
                return;
            }

            // Запуск и ожидание готовности модели идут в фоне
            _ = Task.Run(async () =>
            {
                try
                {
                    ready.TrySetResult(await LaunchAsync());
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[fullline] llama-server is not available: {exception.Message}");
                    ready.TrySetResult(string.Empty);
                }
            });
        }

        private async Task<string> LaunchAsync()
        {
            var dir = ResolveInstallDir();
            var configPath = Path.Combine(dir, "fullline-setup.json");
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine(
                    $"[fullline] {configPath} not found - full line completion components are not installed.");
                return string.Empty;
            }

            var config = JsonSerializer.Deserialize<SetupConfig>(await File.ReadAllTextAsync(configPath));
            if (config is null || string.IsNullOrEmpty(config.ServerPath) || !File.Exists(config.ServerPath))
            {
                Console.Error.WriteLine($"[fullline] invalid setup config: {configPath}");
                return string.Empty;
            }

            installDir = config.InstallDir;
            KillStaleServer(installDir);

            var port = GetFreePort();
            var arguments = BuildServerArguments(config, port);
            var startInfo = new ProcessStartInfo
            {
                FileName = config.ServerPath,
                Arguments = string.Join(" ", arguments.Select(Quote)),
                WorkingDirectory = Path.GetDirectoryName(config.ServerPath) ?? installDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("llama-server process did not start.");
            processExitHandler = (_, _) => Stop();
            AppDomain.CurrentDomain.ProcessExit += processExitHandler;

            // Job Object с kill-on-close: ядро само гасит llama-server, когда
            // LSP-сервер закрывает дескриптор — при любом способе завершения
            // (включая принудительное убийство при закрытии хоста, когда
            // ProcessExit не срабатывает). На не-Windows остаётся ProcessExit.
            job = TryAssignKillOnCloseJob(process);

            // Логи llama-server идут в stderr LSP-сервера и попадают в канал вывода хоста.
            DrainToConsole(process.StandardError);
            DrainToConsole(process.StandardOutput);

            WriteState(installDir, process.Id, port, config.ServerPath);
            Console.Error.WriteLine(
                $"[fullline] started llama-server pid {process.Id} on port {port}, waiting for the model to load...");

            var baseUrl = $"http://127.0.0.1:{port}";
            var deadline = DateTime.UtcNow.AddMinutes(HealthTimeoutMinutes);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new InvalidOperationException(
                        $"llama-server exited with code {process.ExitCode}.");
                try
                {
                    using var response = await HealthClient.GetAsync($"{baseUrl}/health");
                    if (response.IsSuccessStatusCode)
                    {
                        Console.Error.WriteLine($"[fullline] llama-server ready at {baseUrl}.");
                        return baseUrl;
                    }
                    // 503 — модель ещё грузится.
                }
                catch (HttpRequestException)
                {
                    // Порт ещё не слушается.
                }
                await Task.Delay(500);
            }

            throw new TimeoutException("llama-server did not become ready within the timeout.");
        }

        /// <summary>Гасит управляемый llama-server (если он был запущен этим менеджером).</summary>
        public void Stop()
        {
            if (processExitHandler is not null)
            {
                AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
                processExitHandler = null;
            }

            var currentJob = job;
            job = null;
            currentJob?.Dispose();
            var current = process;
            process = null;
            if (current is not null)
            {
                try
                {
                    if (!current.HasExited)
                        current.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Процесс мог завершиться сам.
                }
            }

            if (installDir is not null)
                TryDeleteState(installDir);
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Создаёт Job Object с флагом kill-on-close и помещает в него процесс.
        /// При завершении LSP-сервера дескриптор закрывается автоматически, и
        /// Windows гасит все процессы джоба. null — не-Windows или ошибка API.
        /// </summary>
        private static SafeJobHandle? TryAssignKillOnCloseJob(Process target)
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle.IsInvalid)
                    return null;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };
                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                if (!SetInformationJobObject(
                        handle, 9 /* JobObjectExtendedLimitInformation */,
                        ref info, (uint)length))
                {
                    handle.Dispose();
                    return null;
                }
                if (!AssignProcessToJobObject(handle, target.Handle))
                {
                    handle.Dispose();
                    return null;
                }
                return handle;
            }
            catch
            {
                // Ошибки API не должны ломать запуск сервера: остаётся ProcessExit.
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandle
        {
            public SafeJobHandle() : base(IntPtr.Zero, true)
            {
            }

            public override bool IsInvalid => handle == IntPtr.Zero;

            protected override bool ReleaseHandle() => CloseHandle(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            SafeJobHandle hJob, int jobObjectInformationClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
            uint cbJobObjectInformationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static string ResolveInstallDir()
        {
            var configured = Environment.GetEnvironmentVariable(DirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PascalABC.NET",
                "llama.cpp");
        }

        /// <summary>
        /// Убивает llama-server, оставшийся от упавшего LSP-сервера прошлого
        /// запуска (pid берётся из файла состояния).
        /// </summary>
        private static void KillStaleServer(string installDir)
        {
            var state = TryReadState(installDir);
            if (state is null)
                return;

            try
            {
                var stale = Process.GetProcessById(state.Pid);
                if (stale.ProcessName.Contains("llama", StringComparison.OrdinalIgnoreCase) &&
                    !stale.HasExited)
                {
                    Console.Error.WriteLine($"[fullline] stopping stale llama-server pid {state.Pid}.");
                    stale.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Процесс уже завершился или недоступен — не страшно.
            }

            TryDeleteState(installDir);
        }

        private static void WriteState(string installDir, int pid, int port, string serverPath)
        {
            var state = new ServerState { Pid = pid, Port = port, StartedAtUtc = DateTimeOffset.UtcNow, ServerPath = serverPath };
            File.WriteAllText(
                Path.Combine(installDir, StateFileName),
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static ServerState? TryReadState(string installDir)
        {
            try
            {
                var statePath = Path.Combine(installDir, StateFileName);
                if (!File.Exists(statePath))
                    return null;
                return JsonSerializer.Deserialize<ServerState>(File.ReadAllText(statePath));
            }
            catch
            {
                return null;
            }
        }

        private static void TryDeleteState(string installDir)
        {
            try
            {
                var statePath = Path.Combine(installDir, StateFileName);
                if (File.Exists(statePath))
                    File.Delete(statePath);
            }
            catch
            {
                // Нечего чинить.
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Аргументы llama-server для однострочного дополнения: маленький контекст,
        /// один слот, фиксированные потоки. Тюнинг принадлежит плагину (обновляется
        /// вместе с dll), из конфига берутся только пути, порт подставляется
        /// свободный. -ngl 99 и --flash-attn на CPU-сборках игнорируются или
        /// отключаются сами — команда работает на любом устройстве.
        /// </summary>
        private static string[] BuildServerArguments(SetupConfig config, int port)
        {
            if (string.IsNullOrEmpty(config.ModelPath))
                return ApplyPort(config.ServerArgs ?? Array.Empty<string>(), port);

            return new[]
            {
                "--model", config.ModelPath,
                "--host", "127.0.0.1",
                "--port", port.ToString(),
                "--parallel", "1",
                "--ctx-size", "768",
                "--threads", "4",
                "--threads-batch", "6",
                "--flash-attn", "on",
                "-ngl", "99"
            };
        }

        /// <summary>Подменяет --port в аргументах из конфига на свободный порт.</summary>
        private static string[] ApplyPort(IReadOnlyList<string> serverArgs, int port)
        {
            var arguments = serverArgs.ToList();
            var portIndex = arguments.FindIndex(argument => argument == "--port");
            if (portIndex >= 0 && portIndex + 1 < arguments.Count)
                arguments[portIndex + 1] = port.ToString();
            else
            {
                arguments.Add("--port");
                arguments.Add(port.ToString());
            }
            return arguments.ToArray();
        }

        private static string Quote(string argument) =>
            argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

        // Полный вывод llama-server — только по PABC_LLM_LOG=1: по умолчанию он
        // лишь спамит в канал вывода хоста. Мимо фильтра проходят строки с
        // ошибками — иначе причина отказа llama (битая модель и т.п.) теряется.
        private static readonly bool VerboseLlamaLog =
            Environment.GetEnvironmentVariable("PABC_LLM_LOG") is "1" or "true";

        private static void DrainToConsole(StreamReader reader)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                    {
                        if (VerboseLlamaLog)
                            Console.Error.WriteLine($"[llama] {line}");
                        else if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                 line.Contains("fail", StringComparison.OrdinalIgnoreCase))
                            Console.Error.WriteLine($"[llama] {line}");
                    }
                }
                catch
                {
                    // Поток закрыт вместе с процессом.
                }
            });
        }

        private sealed class ServerState
        {
            public int Pid { get; set; }
            public int Port { get; set; }
            public DateTimeOffset? StartedAtUtc { get; set; }
            public string? ServerPath { get; set; }
        }

        private sealed class SetupConfig
        {
            public string InstallDir { get; set; } = string.Empty;
            public string ServerPath { get; set; } = string.Empty;
            public string? ModelPath { get; set; }
            public string[]? ServerArgs { get; set; }
        }
    }
}
