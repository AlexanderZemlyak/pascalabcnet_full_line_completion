using Newtonsoft.Json;
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
        private readonly string baseUrl;
        private readonly HttpClient client;

        public CompletionClient(HttpClient client, string baseUrl = "http://localhost:8080")
        {
            this.baseUrl = baseUrl.TrimEnd('/');
            this.client = client;
        }

        public async Task<string> GetCompletionAsync(RequestBody requestBody, CancellationToken cancellationToken)
        {
            string json = JsonConvert.SerializeObject(requestBody);
            // Отправляем асинхронный запрос с поддержкой отмены (если пользователь продолжил печатать)
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(baseUrl + "/v1/completions", content, cancellationToken))
            {
                if (!response.IsSuccessStatusCode) return string.Empty;

                string responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<LlamaCompletionResponse>(responseJson);
                if (result.Choices.Count > 0)
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
        [JsonProperty("choices")]
        public List<LlamaChoice> Choices { get; set; }
    }

    internal sealed class LlamaChoice
    {
        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("finish_reason")]
        public string FinishReason { get; set; }
    }
}
