using System.Text;

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FullLineCompletionLspPlugin
{
    internal sealed class PromptData
    {
        public string Prefix { get; private set; }
        public string Suffix { get; private set; }

        public PromptData(string prefix, string suffix)
        {
            Prefix = prefix;
            Suffix = suffix;
        }

        public override string ToString()
        {
            return "<|fim_prefix|>" + Prefix + "<|fim_suffix|>" + Suffix + "<|fim_middle|>";
        }
    }

    internal class PromptBuilder
    {
        private const int MIN_CLEANED_SIZE = 10;
        private const int MAX_CONTENT_TOKENS = 512;
        private const int RESERVED_TOKENS = 64;
        private const double PREFIX_SHARE_WHEN_CUT = 0.7;
        private const double SUFFIX_SHARE_WHEN_CUT = 0.3;

        private readonly TokenizerClient tokenizerClient;

        public PromptBuilder(HttpClient client, string baseUrl = "http://localhost:8080")
        {
            this.tokenizerClient = new TokenizerClient(client, baseUrl);
        }

        public async Task<PromptData?> BuildPromptAsync(string prefixText, string suffixText, CancellationToken cancellationToken)
        {
            // Чистим лишние пробельные символы
            string prefix = CleanLines(prefixText.TrimStart(), preserveLastLine: true);
            string suffix = CleanLines(suffixText.TrimEnd(), preserveFirstLine: true);

            // Комментарии в нашем случае можно оставить
            // Проверку на присутсвие только каркасных слов можно пока опустить,
            // хотя для нормализации со случаем коротких программ с ## можно добавить

            // если меньше, чем минимальный размер, считаем, что подсказывать пока рано
            if (prefix.Length + suffix.Length < MIN_CLEANED_SIZE)
                return null;

            // Обрезка кода по лимиту токенов
            var (cutPrefix, cutSuffix) = await TrimToBudgetAsync(prefix, suffix, cancellationToken);

            return new PromptData(cutPrefix, cutSuffix);
        }

        /// <summary>
        /// Обрезает префикс с конца и суффикс с начала так, чтобы суммарно
        /// они влезли в бюджет.
        ///
        /// Сначала делается гарантированно безопасная проверка: если даже при
        /// пессимистичной оценке "1 символ = 1 токен" мы влезаем в бюджет,
        /// то точная токенизация не нужна — возвращаем как есть.
        /// Только если не влезаем — идём на /tokenize за точными числами.
        /// </summary>
        private async Task<(string Prefix, string Suffix)> TrimToBudgetAsync(
            string prefix, string suffix, CancellationToken ct)
        {
            int budget = MAX_CONTENT_TOKENS - RESERVED_TOKENS;

            // Грубая проверка
            if (prefix.Length + suffix.Length <= budget)
                return (prefix, suffix);

            // Точная токенизация
            int[] prefIds = await tokenizerClient.EncodeAsync(prefix, ct) ?? Array.Empty<int>();
            int[] suffIds = await tokenizerClient.EncodeAsync(suffix, ct) ?? Array.Empty<int>();

            int total = prefIds.Length + suffIds.Length;
            if (total <= budget)
                return (prefix, suffix);

            // Пропорционально урезаем по границам токенов
            int prefBudget = Math.Max(1, (int)(budget * PREFIX_SHARE_WHEN_CUT));
            int suffBudget = Math.Max(1, (int)(budget * SUFFIX_SHARE_WHEN_CUT));

            string cutPrefix = prefix;
            string cutSuffix = suffix;
            // Префикс оставляем с конца
            if (prefIds.Length > 0)
            {
                int prefStart = Math.Max(0, prefIds.Length - prefBudget);
                var prefSlice = Slice(prefIds, prefStart, prefIds.Length);
                cutPrefix = await tokenizerClient.DecodeAsync(prefSlice, ct) ?? prefix;
            }
            // Суффикс оставляем с начала
            if (suffIds.Length > 0)
            {
                int suffEnd = Math.Min(suffIds.Length, suffBudget);
                var suffSlice = Slice(suffIds, 0, suffEnd);
                cutSuffix = await tokenizerClient.DecodeAsync(suffSlice, ct) ?? suffix;
            }

            return (cutPrefix, cutSuffix);
        }

        private int[] Slice(int[] src, int start, int end)
        {
            int len = end - start;
            if (len <= 0) return Array.Empty<int>();
            var dst = new int[len];
            Array.Copy(src, start, dst, 0, len);
            return dst;
        }

        private string CleanLines(string code, bool preserveFirstLine = false, bool preserveLastLine = false)
        {
            var sb = new StringBuilder(code.Length);
            int lineStart = 0;
            int len = code.Length;
            bool first = true;
            int lineIndex = 0;

            int totalLines = 1;
            for (int i = 0; i < len; i++)
                if (code[i] == '\n') totalLines++;

            for (int i = 0; i <= len; i++)
            {
                if (i != len && code[i] != '\n')
                    continue;

                bool isFirstLine = lineIndex == 0;
                bool isLastLine = lineIndex == totalLines - 1;
                bool preserve = (preserveFirstLine && isFirstLine)
                             || (preserveLastLine && isLastLine);

                if (preserve)
                {
                    string preservedLine;
                    if (i != 0 && code[i - 1] == '\r')
                        preservedLine = code.Substring(lineStart, i - lineStart - 1);
                    else
                        preservedLine = code.Substring(lineStart, i - lineStart);
                    // Сохраняем строку как есть: без обрезки пробелов
                    if (!first) sb.Append('\n');
                    sb.Append(preservedLine);
                    first = false;
                }
                else
                {
                    int end = i;
                    if (end > lineStart && code[end - 1] == '\r')
                        end--;
                    while (end > lineStart && char.IsWhiteSpace(code[end - 1]))
                        end--;
                    if (end > lineStart)
                    {
                        if (!first) sb.Append('\n');
                        sb.Append(code, lineStart, end - lineStart);
                        first = false;
                    }
                }
                lineStart = i + 1;
                lineIndex++;
            }

            return sb.ToString();
        }
    }
}
