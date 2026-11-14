# PascalABC.NET Full Line Completion

LLM-плагин full-line completion для LSP-сервера PascalABC.NET
(PascalABCNet.LanguageServer), только .NET 10:

> Архитектура, политика обновлений и удаления: [docs/design-fullline-completion.md](docs/design-fullline-completion.md).

- `src/FullLineCompletionLspPlugin` — регистрирует `IInlineCompletionProvider`
  через плагин-механику LSP-сервера. LLM-ядро (FIM-промпт, клиенты llama-server
  `/v1/completions`, `/tokenize`, `/detokenize`).

Контракты (`IInlineCompletionProvider` из `CodeCompletionContracts` и
`ILanguageServerPlugin` из `LSP_PluginsAPI/PascalABCNet.LanguageServer.PluginApi`)
берутся ссылками на проекты сабмодуля `pascalabcnet/`

## Сборка

    dotnet build src/FullLineCompletionLspPlugin/FullLineCompletionLspPlugin.csproj

## Установка LLM-рантайма (llama.cpp + модель)

`src/FullLineCompletionSetup` — консольный инструмент (.NET 10, без внешних
зависимостей) для установки рантайма инлайн-дополнений. Он:

- определяет ОС и устройство по приоритету: дискретная GPU → встроенная GPU →
  CPU (macOS — Metal); для GPU скачивается Vulkan-сборка (подходит и для
  дискретных, и для встроенных), для NVIDIA есть `--device cuda`, без GPU —
  CPU-сборка;
- скачивает последний официальный релиз llama.cpp с GitHub (с учётом их
  схемы «nightly»-тегов);
- скачивает модель (`--model-url`, `--model-file` или переменная окружения
  `PABC_FULLLINE_MODEL_URL`) в папку с llama.cpp;
- пишет `fullline-setup.json` с путями к `llama-server`, к модели и готовыми
  аргументами запуска — этот файл читают и VS Code аддон, и legacy IDE.

Команда установки одинакова для обоих хостов:

    dotnet FullLineCompletionSetup.dll --dir <каталог> [--model-url <url>]

Опции: `--device auto|discretegpu|integratedgpu|cpu|vulkan|cuda|metal`,
`--model-name <имя>`, `--no-model`, `--dry-run`, `--quiet`.

## Установка LSP-плагина

Скопировать `FullLineCompletionLspPlugin.dll` в каталог плагинов LSP-сервера
(каталог передается серверу аргументом --plugins-dir на старте)

Сервер загружает сборки `*LspPlugin.dll` при старте и объявляет
`experimental.inlineCompletionProvider` в initialize. Запросы:
`textDocument/inlineCompletion` (стандарт LSP 3.18) и `pascalabc/inlineCompletion`
(зеркало для клиентов без поддержки 3.18).

## Конфигурация LLM

Адрес llama-server задаётся переменной окружения `PABC_LLM_URL`
(по умолчанию `http://localhost:8080`).
