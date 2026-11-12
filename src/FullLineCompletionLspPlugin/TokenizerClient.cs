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
    /// <summary>
    /// Клиент для эндпоинтов /tokenize и /detokenize llama-server.
    /// Кэширует результаты по хэшу строки.
    /// </summary>
    internal sealed class TokenizerClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private class LRUCache
        {
            private const int CacheCapacity = 32;
            private readonly Dictionary<(int, string), int[]> cache = new Dictionary<(int, string), int[]>();
            private readonly LinkedList<string> lru = new LinkedList<string>();

            public bool TryGetValue(string key, out int[]? value)
            {
                if (cache.TryGetValue((key.Length, key), out var cached))
                {
                    lru.Remove(key);
                    lru.AddFirst(key);
                    value = cached;
                    return true;
                }
                value = null;
                return false;
            }

            public int[] this[string key]
            {
                set
                {
                    cache[(key.Length, key)] = value;
                    lru.AddFirst(key);
                    if (lru.Count > CacheCapacity)
                    {
                        var last = lru.Last!.Value;
                        lru.RemoveLast();
                        cache.Remove((last.Length, last));
                    }
                }
            }
        }

        private readonly string baseUrl;
        private readonly HttpClient client;
        private readonly LRUCache cache;

        public TokenizerClient(HttpClient client, string baseUrl = "http://localhost:8080")
        {
            this.baseUrl = baseUrl.TrimEnd('/');
            this.client = client;
            this.cache = new LRUCache();
        }

        /// <summary>
        /// Возвращает массив id токенов. При ошибке возвращает null
        /// </summary>
        public async Task<int[]?> EncodeAsync(string text, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<int>();

            if (cache.TryGetValue(text, out var cached))
                return cached;

            var ids = await EncodeRemoteAsync(text, ct);
            if (ids == null)
                return null;

            cache[text] = ids;
            return ids;
        }

        /// <summary>
        /// Декодирует id обратно в строку. При ошибке возвращает null.
        /// </summary>
        public async Task<string?> DecodeAsync(int[] ids, CancellationToken ct)
        {
            if (ids == null || ids.Length == 0)
                return string.Empty;

            var body = new { tokens = ids };
            string json = JsonSerializer.Serialize(body);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await client.PostAsync(baseUrl + "/detokenize", content, ct))
            {
                if (!response.IsSuccessStatusCode)
                    return null;
                string responseJson = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<DetokenizeResponse>(responseJson, JsonOptions);
                return result?.Content;
            }
        }

        private async Task<int[]?> EncodeRemoteAsync(string text, CancellationToken ct)
        {
            try
            {
                var body = new { content = text, parse_special = true };
                string json = JsonSerializer.Serialize(body);
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync(baseUrl + "/tokenize", content, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        return null;
                    string responseJson = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<TokenizeResponse>(responseJson, JsonOptions);
                    return result?.Tokens;
                }
            }
            catch (TaskCanceledException)
            {
                throw; // отмену пробрасываем наверх — это нормальный сценарий
            }
            catch
            {
                return null; // сервер недоступен
            }
        }

        private sealed class TokenizeResponse
        {
            [JsonPropertyName("tokens")]
            public int[] Tokens { get; set; } = Array.Empty<int>();
        }

        private sealed class DetokenizeResponse
        {
            [JsonPropertyName("content")]
            public string Content { get; set; } = string.Empty;
        }
    }
}
