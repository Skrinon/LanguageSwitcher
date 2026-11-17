using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Фильтр ненормативной лексики и популярного сленга (RU, UK, EN).
    /// Предотвращает ошибочную замену мата/сленга на обычные слова (например, «поехать» / «проехать»)
    /// и не допускает предложения ненормативных слов в качестве исправления опечаток.
    /// </summary>
    public sealed class ProfanityFilterService
    {
        private const string FilterFileName = "profanity_filter.txt";

        private readonly string _filterFilePath;
        private readonly bool _isDefaultDirectory;
        private readonly HashSet<string> _exactWords = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _rootPatterns = new List<string>();

        // Приставки русского и украинского языков для морфологического анализа основ (по-, про-, за- и т.д.)
        private static readonly string[] SlavicPrefixes =
        {
            "пере", "пона", "при", "подъ", "под", "під'", "під", "надъ", "над",
            "разъ", "раз", "рас", "роз'", "роз", "від'", "від", "объ", "обо", "об",
            "отъ", "ото", "от", "въ", "вы", "ви", "съ", "со", "до", "за", "на",
            "по", "про", "из", "ис", "не", "у", "в", "з'", "з", "с", "о", "а"
        };

        // Характерные славянские матерные корни, допускаемые строго в начале основы после отсечения приставки
        private static readonly string[] StemStartRoots =
        {
            "еб", "ёб", "єб", "їб", "ебл", "ебн", "ёбн", "еба", "ебу", "ебе", "ебё", "еби", "ебы",
            "бля", "хер", "сра", "муд", "сук", "суч"
        };

        // Безопасные окончания для основ на еб-/ёб-/єб-/їб-, исключающие случайные слова
        private static readonly HashSet<char> ValidStemFollowerChars = new HashSet<char>(
            "аеёиоуыэюяіїєлншткьъ");

        public ProfanityFilterService(string? customDictionariesDirectory = null)
        {
            _isDefaultDirectory = string.IsNullOrWhiteSpace(customDictionariesDirectory);
            string dir = _isDefaultDirectory
                ? Path.Combine(AppContext.BaseDirectory, "Dictionaries")
                : customDictionariesDirectory!;

            _filterFilePath = Path.Combine(dir, FilterFileName);
            LoadFilter();
        }

        public void LoadFilter()
        {
            _exactWords.Clear();
            _rootPatterns.Clear();

            if (File.Exists(_filterFilePath))
            {
                ParseFilterLines(File.ReadLines(_filterFilePath, Encoding.UTF8));
                return;
            }

            if (_isDefaultDirectory)
            {
                using Stream? resourceStream = typeof(ProfanityFilterService).Assembly
                    .GetManifestResourceStream($"LanguageSwitcher.Dictionaries.{FilterFileName}");
                if (resourceStream != null)
                {
                    using var reader = new StreamReader(resourceStream, Encoding.UTF8);
                    ParseFilterLines(ReadLinesFromReader(reader));
                }
            }
        }

        private void ParseFilterLines(IEnumerable<string> lines)
        {
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string normalized = line
                    .Replace('\u2019', '\'')
                    .Replace('\u02BC', '\'')
                    .ToLowerInvariant();

                if (normalized.StartsWith("root:", StringComparison.Ordinal))
                {
                    string root = normalized.Substring(5).Trim();
                    if (!string.IsNullOrEmpty(root))
                    {
                        _rootPatterns.Add(root);
                    }
                }
                else
                {
                    _exactWords.Add(normalized);
                }
            }
        }

        private static IEnumerable<string> ReadLinesFromReader(StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }

        /// <summary>
        /// Возвращает true, если слово распознано как ненормативная лексика или сленг.
        /// </summary>
        public bool IsProfanityOrSlang(string? word)
        {
            if (string.IsNullOrWhiteSpace(word))
            {
                return false;
            }

            string normalized = word.Trim()
                .Replace('\u2019', '\'')
                .Replace('\u02BC', '\'')
                .ToLowerInvariant();

            if (normalized.Length < 2)
            {
                return false;
            }

            // 1. Точное совпадение со встроенным словарём сленга и словоформ
            if (_exactWords.Contains(normalized))
            {
                return true;
            }

            // 2. Поиск однозначных корней внутри слова (например, "хуй", "пизд", "бляд", "fuck")
            foreach (string root in _rootPatterns)
            {
                if (normalized.Contains(root, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // 3. Морфологическая проверка: снимаем до 2 приставок (по-, про-, за-, на-, вы-, от-, пере-, до-, с-, раз-, у-, об- и др.)
            // и проверяем, начинается ли корень основы с характерного матерного корня (не срабатывает на "поехать" / "проехать")
            if (MatchesStemAfterPrefixStrip(normalized))
            {
                return true;
            }

            foreach (string prefix1 in SlavicPrefixes)
            {
                if (normalized.Length > prefix1.Length + 2 && normalized.StartsWith(prefix1, StringComparison.Ordinal))
                {
                    string stem1 = normalized.Substring(prefix1.Length);
                    if (_exactWords.Contains(stem1) || MatchesStemAfterPrefixStrip(stem1))
                    {
                        return true;
                    }

                    // Поддержка двойных приставок (например, по-на-, пере-за-, не-до-)
                    foreach (string prefix2 in SlavicPrefixes)
                    {
                        if (stem1.Length > prefix2.Length + 2 && stem1.StartsWith(prefix2, StringComparison.Ordinal))
                        {
                            string stem2 = stem1.Substring(prefix2.Length);
                            if (_exactWords.Contains(stem2) || MatchesStemAfterPrefixStrip(stem2))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool MatchesStemAfterPrefixStrip(string stem)
        {
            foreach (string root in StemStartRoots)
            {
                if (!stem.StartsWith(root, StringComparison.Ordinal))
                {
                    continue;
                }

                // Для корней "еб"/"ёб"/"єб"/"їб" проверяем длину и допустимое продолжение (отсекает "ех-" в "поехать")
                if (root == "еб" || root == "ёб" || root == "єб" || root == "їб")
                {
                    if (stem.Length == 2)
                    {
                        return true;
                    }

                    if (ValidStemFollowerChars.Contains(stem[2]))
                    {
                        return true;
                    }
                }
                else if (root == "бля")
                {
                    if (stem.Length == 3 || stem.StartsWith("бляд", StringComparison.Ordinal)
                        || stem.StartsWith("блять", StringComparison.Ordinal)
                        || stem.StartsWith("блях", StringComparison.Ordinal)
                        || stem.StartsWith("бляц", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (root == "хер")
                {
                    if (stem.Length == 3 || stem.StartsWith("херн", StringComparison.Ordinal)
                        || stem.StartsWith("херов", StringComparison.Ordinal)
                        || stem.StartsWith("херя", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (root == "сра")
                {
                    if (stem.StartsWith("срат", StringComparison.Ordinal)
                        || stem.StartsWith("сран", StringComparison.Ordinal)
                        || stem.StartsWith("срал", StringComparison.Ordinal)
                        || stem.StartsWith("срак", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (root == "муд")
                {
                    if (stem.StartsWith("муда", StringComparison.Ordinal)
                        || stem.StartsWith("муди", StringComparison.Ordinal)
                        || stem.StartsWith("мудл", StringComparison.Ordinal)
                        || stem.StartsWith("мудо", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (root == "сук" || root == "суч")
                {
                    if (stem.Equals("сук", StringComparison.Ordinal)
                        || stem.Equals("сука", StringComparison.Ordinal)
                        || stem.Equals("суки", StringComparison.Ordinal)
                        || stem.Equals("суку", StringComparison.Ordinal)
                        || stem.Equals("суке", StringComparison.Ordinal)
                        || stem.Equals("сукой", StringComparison.Ordinal)
                        || stem.StartsWith("сучк", StringComparison.Ordinal)
                        || stem.StartsWith("сучар", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else
                {
                    return true;
                }
            }

            return false;
        }
    }
}
