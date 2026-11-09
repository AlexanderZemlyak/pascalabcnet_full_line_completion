# PascalABC.NET Full Line Completion

LLM-плагин full-line completion для LSP-сервера PascalABC.NET
(PascalABCNet.LanguageServer), только .NET 10:

- `src/FullLineCompletionLspPlugin` — регистрирует `IInlineCompletionProvider`
  через плагин-механику LSP-сервера. LLM-ядро (FIM-промпт, клиенты llama-server
  `/v1/completions`, `/tokenize`, `/detokenize`) перенесено сюда из Legacy-плагина.

Контракты (`IInlineCompletionProvider` из `CodeCompletionContracts` и
`ILanguageServerPlugin` из `plugins/PascalABCNet.LanguageServer.PluginApi`)
берутся ссылками на проекты сабмодуля `pascalabcnet/`

## Сборка

    dotnet build src/FullLineCompletionLspPlugin/FullLineCompletionLspPlugin.csproj

## Установка LSP-плагина

Скопировать `FullLineCompletionLspPlugin.dll` и `Newtonsoft.Json.dll` в каталог
плагинов LSP-сервера:

- VS Code: `<расширение>/bin/net10/plugins/` (или передать серверу
  `--plugins-dir <путь>`);
- .NET 10 IDE: `<каталог сервера>/plugins/`.

Сервер загружает сборки `*Plugin.dll` при старте и объявляет
`experimental.inlineCompletionProvider` в initialize. Запросы:
`textDocument/inlineCompletion` (стандарт LSP 3.18) и `pascalabc/inlineCompletion`
(зеркало для клиентов без поддержки 3.18).

## Конфигурация LLM

Адрес llama-server задаётся переменной окружения `PABC_LLM_URL`
(по умолчанию `http://localhost:8080`).
