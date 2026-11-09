using System.Text.Json.Serialization;
using System.Net.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using PascalABCCompiler.Contracts.CodeCompletion;

namespace FullLineCompletionLspPlugin
{
    public class FullLineCompletionProvider : IInlineCompletionProvider
    {
        public bool IsEnabled => true;

        private readonly HttpClient client;
        private readonly PromptBuilder promptBuilder;
        private readonly CompletionClient completionClient;

        private const int MAX_TOKENS_TO_GENERATE = 32;
        private const float TEMPERATURE_PARAM = 0.3f;
        private const float MIN_TOKEN_PROBABILITY_DYN_THRESHOLD = 0.1f;
        private const int SERVER_RESPONSE_AWAIT_TIMEOUT = 5;

        public FullLineCompletionProvider()
        {
            client = new HttpClient()
            {
                Timeout = TimeSpan.FromSeconds(SERVER_RESPONSE_AWAIT_TIMEOUT)
            };

            var baseUrl = Environment.GetEnvironmentVariable("PABC_LLM_URL") ?? "http://localhost:8080";

            completionClient = new CompletionClient(client, baseUrl);

            promptBuilder = new PromptBuilder(client, baseUrl);
        }

        public async Task<string> GetCompletionAsync(CodeCompletionContext context, CancellationToken cancellationToken)
        {
            try
            {
                // Формируем чистый FIM-промпт
                var promptData = await promptBuilder.BuildPromptAsync(
                    context.Prefix,
                    context.Suffix,
                    cancellationToken);

                if (promptData == null)
                    return string.Empty;

                var requestBody = new RequestBody
                {
                    Prompt = promptData.ToString(),
                    MaxNewTokens = MAX_TOKENS_TO_GENERATE,
                    StopStrings = new[] {
                        "\r",
                        "\n",
                        "<|endoftext|>",
                        "<|fim_prefix|>",
                        "<|fim_middle|>",
                        "<|fim_suffix|>",
                        "<|fim_pad|>"
                    },
                    Temperature = TEMPERATURE_PARAM,
                    MinP = MIN_TOKEN_PROBABILITY_DYN_THRESHOLD,
                    ParseSpecial = true
                };

                var completedCode = await completionClient.GetCompletionAsync(requestBody, cancellationToken);

                // Постпроверки дополнения
                // TODO: доработать
                var trimmedPrefix = promptData.Prefix.TrimEnd();
                var indexOfLastPrefixLine = trimmedPrefix.LastIndexOf('\n') + 1;
                var trimmedSuffix = promptData.Suffix.TrimStart();
                var indexOfFirstSuffixLine = trimmedSuffix.IndexOf('\n') + 1;

                var completedLine = completedCode;

                if (completedLine == trimmedPrefix.Substring(indexOfLastPrefixLine, trimmedPrefix.Length - indexOfLastPrefixLine).Trim() ||
                    completedLine == trimmedSuffix.Substring(0, indexOfFirstSuffixLine).Trim())
                {
                    return string.Empty;
                }

                return completedCode;
            }
            catch (TaskCanceledException)
            {
                // Запрос был отменен
                throw;
            }
            catch
            {
                // Локальный сервер упал или не запущен — пока молча пропускаем
                return string.Empty;
            }
        }
    }

    internal sealed class RequestBody
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("max_tokens")]
        public int MaxNewTokens { get; set; }

        [JsonPropertyName("stop")]
        public string[] StopStrings { get; set; } = Array.Empty<string>();

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; }

        [JsonPropertyName("min_p")]
        public float MinP { get; set; }

        [JsonPropertyName("parse_special")]
        public bool ParseSpecial { get; set; }

        [JsonPropertyName("logit_bias")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[][]? LogitBias { get; set; }
    }
}
