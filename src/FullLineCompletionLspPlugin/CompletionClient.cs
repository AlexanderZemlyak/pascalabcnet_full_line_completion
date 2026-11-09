using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FullLineCompletionLspPlugin
{
    internal class CompletionClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly string baseUrl;
        private readonly HttpClient client;

        public CompletionClient(HttpClient client, string baseUrl = "http://localhost:8080")
        {
            this.baseUrl = baseUrl.TrimEnd('/');
            this.client = client;
        }

        public async Task<string> GetCompletionAsync(RequestBody requestBody, CancellationToken cancellationToken)
        {
            string json = JsonSerializer.Serialize(requestBody);
            // Отправляем асинхронный запрос с поддержкой отмены (если пользователь продолжил печатать)
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(baseUrl + "/v1/completions", content, cancellationToken))
            {
                if (!response.IsSuccessStatusCode) return string.Empty;

                string responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<LlamaCompletionResponse>(responseJson, JsonOptions);
                if (result != null && result.Choices.Count > 0)
                {
                    string completedText = result.Choices[0].Text;
                    return completedText;
                }

                return string.Empty;
            }
        }
    }

    internal sealed class LlamaCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<LlamaChoice> Choices { get; set; } = new List<LlamaChoice>();
    }

    internal sealed class LlamaChoice
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("finish_reason")]
        public string FinishReason { get; set; } = string.Empty;
    }
}
