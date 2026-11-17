using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Результат автоматического переключения раскладки слова (Приоритет 2).
    /// </summary>
    public sealed class AutoSwitchWordResult
    {
        public bool ShouldSwitch { get; }
        public string OriginalWord { get; }
        public string ConvertedWord { get; }
        public SupportedLanguage SourceLanguage { get; }
        public SupportedLanguage TargetLanguage { get; }

        public AutoSwitchWordResult(
            bool shouldSwitch,
            string originalWord,
            string convertedWord,
            SupportedLanguage sourceLanguage,
            SupportedLanguage targetLanguage)
        {
            ShouldSwitch = shouldSwitch;
            OriginalWord = originalWord;
            ConvertedWord = convertedWord;
            SourceLanguage = sourceLanguage;
            TargetLanguage = targetLanguage;
        }
    }

    /// <summary>
    /// Результат исправления механической опечатки (Приоритет 3).
    /// </summary>
    public sealed class TypoCorrectionResult
    {
        public bool ShouldCorrect { get; }
        public string OriginalWord { get; }
        public string CorrectedWord { get; }
        public SupportedLanguage Language { get; }
        public double ErrorWeight { get; }

        public TypoCorrectionResult(
            bool shouldCorrect,
            string originalWord,
            string correctedWord,
            SupportedLanguage language,
            double errorWeight = 0.0)
        {
            ShouldCorrect = shouldCorrect;
            OriginalWord = originalWord;
            CorrectedWord = correctedWord;
            Language = language;
            ErrorWeight = errorWeight;
        }
    }

    internal readonly struct RankedWord
    {
        public string Word { get; }
        public int Rank { get; }

        public RankedWord(string word, int rank)
        {
            Word = word;
            Rank = rank;
        }
    }

    /// <summary>
    /// Управляет встроенными офлайн-словарями (Dictionaries/*.txt), пользовательским словарём (user_dictionary.json),
    /// списком исключений (ignored_words.json), фильтром ненормативной лексики, исправлением двойных заглавных,
    /// автопереключением раскладки и взвешенным клавиатурным исправлением опечаток.
    /// </summary>
    public sealed class DictionaryService
    {
        // Веса клавиатурных операций
        private const double WeightAdjacentDoubleHitInsertion = 0.45;
        private const double WeightAdjacentSubstitution = 0.55;
        private const double WeightTransposition = 0.55;
        private const double WeightFastOmission = 0.75;
        private const double WeightNonAdjacentEdit = 1.25;

        // Пороги суммарного веса ошибок
        private const double MaxWeightShortWord = 0.75;   // Для слов 3-4 символа (до 1 ошибки)
        private const double MaxWeightNormalTyping = 1.15; // Обычный темп печати (>= 5 символов)
        private const double MaxWeightBurstTyping = 1.45;  // Скоростная печать (>= 5 символов)

        private readonly SettingsManager _settingsManager;
        private readonly ProfanityFilterService _profanityFilter;
        private readonly string _dictionariesDirectory;
        private readonly bool _isDefaultDirectory;
        private readonly object _lock = new object();

        // Встроенные частотные словари: слово -> ранг
        private readonly Dictionary<SupportedLanguage, Dictionary<string, int>> _builtInRanks =
            new Dictionary<SupportedLanguage, Dictionary<string, int>>
            {
                [SupportedLanguage.English] = new Dictionary<string, int>(StringComparer.Ordinal),
                [SupportedLanguage.Russian] = new Dictionary<string, int>(StringComparer.Ordinal),
                [SupportedLanguage.Ukrainian] = new Dictionary<string, int>(StringComparer.Ordinal)
            };

        // Индексы для мгновенного поиска по длине слова
        private readonly Dictionary<SupportedLanguage, Dictionary<int, List<RankedWord>>> _builtInByLength =
            new Dictionary<SupportedLanguage, Dictionary<int, List<RankedWord>>>
            {
                [SupportedLanguage.English] = new Dictionary<int, List<RankedWord>>(),
                [SupportedLanguage.Russian] = new Dictionary<int, List<RankedWord>>(),
                [SupportedLanguage.Ukrainian] = new Dictionary<int, List<RankedWord>>()
            };

        private readonly Dictionary<SupportedLanguage, HashSet<string>> _userWordSets =
            new Dictionary<SupportedLanguage, HashSet<string>>
            {
                [SupportedLanguage.English] = new HashSet<string>(StringComparer.Ordinal),
                [SupportedLanguage.Russian] = new HashSet<string>(StringComparer.Ordinal),
                [SupportedLanguage.Ukrainian] = new HashSet<string>(StringComparer.Ordinal)
            };

        private readonly List<UserDictionaryEntry> _userEntries = new List<UserDictionaryEntry>();
        private readonly HashSet<string> _ignoredWords = new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<string> GamingAndTechTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wasd", "awp", "gg", "wp", "gl", "hf", "afk", "ez", "fps", "rpg", "npc",
            "lol", "omg", "idk", "brb", "btw", "gtg", "xd", "ctrl", "alt", "shift",
            "tab", "esc", "del", "http", "https", "www", "ftp", "ssh", "sql", "json",
            "xml", "html", "css", "api", "url", "exe", "dll", "pdf", "png", "jpg"
        };

        private static readonly Dictionary<SupportedLanguage, string> Alphabets =
            new Dictionary<SupportedLanguage, string>
            {
                [SupportedLanguage.English] = "abcdefghijklmnopqrstuvwxyz",
                [SupportedLanguage.Russian] = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя",
                [SupportedLanguage.Ukrainian] = "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя'"
            };

        private static readonly HashSet<char> SlavicVowels = new HashSet<char>("аеёиоуыэюяіїє");

        // Полная матрица физических координат клавиш для EN, RU, UA (для расчёта соседства по горизонтали, вертикали и диагонали)
        private static readonly Dictionary<SupportedLanguage, Dictionary<char, (double Row, double Col)>> KeyPositions =
            BuildKeyPositions();

        // Плоская O(1) матрица смежности клавиш (384 x 384) для мгновенной проверки в DP без хэширования Dictionary
        private static readonly Dictionary<SupportedLanguage, bool[]> FastAdjacencyTables =
            BuildFastAdjacencyTables();

        public bool IsLoaded { get; private set; }

        public DictionaryService(
            SettingsManager settingsManager,
            ProfanityFilterService? profanityFilter = null,
            string? customDictionariesDirectory = null)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _isDefaultDirectory = string.IsNullOrWhiteSpace(customDictionariesDirectory);
            _dictionariesDirectory = _isDefaultDirectory
                ? Path.Combine(AppContext.BaseDirectory, "Dictionaries")
                : customDictionariesDirectory!;
            _profanityFilter = profanityFilter ?? new ProfanityFilterService(customDictionariesDirectory);

            ReloadUserDataFromDisk();
        }

        /// <summary>
        /// Конструктор обратной совместимости с указанием папки словарей.
        /// </summary>
        public DictionaryService(SettingsManager settingsManager, string customDictionariesDirectory)
            : this(settingsManager, new ProfanityFilterService(customDictionariesDirectory), customDictionariesDirectory)
        {
        }

        /// <summary>
        /// Асинхронно загружает встроенные офлайн-словари (en.txt, ru.txt, uk.txt) в память и индексирует их по длине.
        /// </summary>
        public async Task LoadBuiltInDictionariesAsync()
        {
            var files = new (SupportedLanguage Language, string FileName)[]
            {
                (SupportedLanguage.English, "en.txt"),
                (SupportedLanguage.Russian, "ru.txt"),
                (SupportedLanguage.Ukrainian, "uk.txt")
            };

            var loaded = await Task.Run(() =>
            {
                var ranksResult = new Dictionary<SupportedLanguage, Dictionary<string, int>>();
                var byLenResult = new Dictionary<SupportedLanguage, Dictionary<int, List<RankedWord>>>();

                foreach ((SupportedLanguage lang, string fileName) in files)
                {
                    var map = new Dictionary<string, int>(StringComparer.Ordinal);
                    var byLen = new Dictionary<int, List<RankedWord>>();
                    string path = Path.Combine(_dictionariesDirectory, fileName);

                    if (File.Exists(path))
                    {
                        IndexDictionaryLines(File.ReadLines(path, Encoding.UTF8), map, byLen);
                    }
                    else if (_isDefaultDirectory)
                    {
                        using Stream? resourceStream = typeof(DictionaryService).Assembly
                            .GetManifestResourceStream($"LanguageSwitcher.Dictionaries.{fileName}");
                        if (resourceStream != null)
                        {
                            using var reader = new StreamReader(resourceStream, Encoding.UTF8);
                            IndexDictionaryLines(ReadLinesFromReader(reader), map, byLen);
                        }
                    }

                    ranksResult[lang] = map;
                    byLenResult[lang] = byLen;
                }

                return (ranksResult, byLenResult);
            }).ConfigureAwait(false);

            lock (_lock)
            {
                foreach (KeyValuePair<SupportedLanguage, Dictionary<string, int>> pair in loaded.ranksResult)
                {
                    _builtInRanks[pair.Key] = pair.Value;
                }

                foreach (KeyValuePair<SupportedLanguage, Dictionary<int, List<RankedWord>>> pair in loaded.byLenResult)
                {
                    _builtInByLength[pair.Key] = pair.Value;
                }

                IsLoaded = true;
            }
        }

        private static void IndexDictionaryLines(
            IEnumerable<string> lines,
            Dictionary<string, int> map,
            Dictionary<int, List<RankedWord>> byLen)
        {
            int rank = 0;
            foreach (string rawLine in lines)
            {
                string w = NormalizeWord(rawLine);
                if (string.IsNullOrEmpty(w) || map.ContainsKey(w))
                {
                    continue;
                }

                map[w] = rank;
                if (!byLen.TryGetValue(w.Length, out List<RankedWord>? bucket))
                {
                    bucket = new List<RankedWord>();
                    byLen[w.Length] = bucket;
                }

                bucket.Add(new RankedWord(w, rank));
                rank++;
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

        public void ReloadUserDataFromDisk()
        {
            List<string> ignored = _settingsManager.LoadIgnoredWords();
            List<UserDictionaryEntry> userDict = _settingsManager.LoadUserDictionary();

            lock (_lock)
            {
                _ignoredWords.Clear();
                foreach (string word in ignored)
                {
                    string normalized = NormalizeWord(word);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _ignoredWords.Add(normalized);
                    }
                }

                ApplyUserEntriesLocked(userDict);
            }
        }

        public int GetBuiltInWordCount()
        {
            lock (_lock)
            {
                return _builtInRanks.Values.Sum(dict => dict.Count);
            }
        }

        public int GetUserWordCount()
        {
            lock (_lock)
            {
                return _userEntries.Count;
            }
        }

        public List<string> GetIgnoredWordsSnapshot()
        {
            lock (_lock)
            {
                return _ignoredWords.OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }

        public List<UserDictionaryEntry> GetUserDictionarySnapshot()
        {
            lock (_lock)
            {
                return _userEntries
                    .Select(e => new UserDictionaryEntry { Word = e.Word, Language = e.Language })
                    .OrderBy(e => e.Word, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        public bool IsIgnored(string word)
        {
            string normalized = NormalizeWord(word);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            lock (_lock)
            {
                return _ignoredWords.Contains(normalized);
            }
        }

        public int AddIgnoredWords(IEnumerable<string> words)
        {
            int added = 0;
            lock (_lock)
            {
                foreach (string raw in words)
                {
                    string normalized = NormalizeWord(raw);
                    if (!string.IsNullOrEmpty(normalized) && _ignoredWords.Add(normalized))
                    {
                        added++;
                    }
                }

                if (added > 0)
                {
                    SaveIgnoredWordsLocked();
                }
            }

            return added;
        }

        public bool RemoveIgnoredWord(string word)
        {
            string normalized = NormalizeWord(word);
            lock (_lock)
            {
                if (_ignoredWords.Remove(normalized))
                {
                    SaveIgnoredWordsLocked();
                    return true;
                }
            }

            return false;
        }

        public void ReplaceIgnoredWords(IEnumerable<string> words)
        {
            lock (_lock)
            {
                _ignoredWords.Clear();
                foreach (string raw in words)
                {
                    string normalized = NormalizeWord(raw);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _ignoredWords.Add(normalized);
                    }
                }

                SaveIgnoredWordsLocked();
            }
        }

        public void ClearIgnoredWords()
        {
            lock (_lock)
            {
                _ignoredWords.Clear();
                SaveIgnoredWordsLocked();
            }
        }

        public (int AddedCount, int SkippedDuplicatesCount) AddUserWords(
            IEnumerable<string> words,
            SupportedLanguage language)
        {
            int added = 0;
            int skipped = 0;

            lock (_lock)
            {
                HashSet<string> set = _userWordSets[language];
                foreach (string raw in words)
                {
                    string normalized = NormalizeWord(raw);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        continue;
                    }

                    if (set.Add(normalized))
                    {
                        _userEntries.Add(new UserDictionaryEntry
                        {
                            Word = normalized,
                            Language = language
                        });
                        added++;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                if (added > 0)
                {
                    SaveUserDictionaryLocked();
                }
            }

            return (added, skipped);
        }

        public bool RemoveUserWord(string word, SupportedLanguage language)
        {
            string normalized = NormalizeWord(word);
            lock (_lock)
            {
                if (_userWordSets[language].Remove(normalized))
                {
                    _userEntries.RemoveAll(e => e.Language == language
                        && string.Equals(e.Word, normalized, StringComparison.Ordinal));
                    SaveUserDictionaryLocked();
                    return true;
                }
            }

            return false;
        }

        public void ReplaceUserDictionary(IEnumerable<UserDictionaryEntry> entries)
        {
            lock (_lock)
            {
                ApplyUserEntriesLocked(entries);
                SaveUserDictionaryLocked();
            }
        }

        public void ClearUserDictionary()
        {
            lock (_lock)
            {
                _userEntries.Clear();
                foreach (HashSet<string> set in _userWordSets.Values)
                {
                    set.Clear();
                }

                SaveUserDictionaryLocked();
            }
        }

        public bool ContainsWord(SupportedLanguage language, string word)
        {
            string normalized = NormalizeWord(word);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            lock (_lock)
            {
                return _userWordSets[language].Contains(normalized)
                    || _builtInRanks[language].ContainsKey(normalized);
            }
        }

        /// <summary>
        /// Исправляет случайную вторую заглавную букву в начале слова (ПРивет -> Привет, HEllo -> Hello).
        /// Не затрагивает слова целиком в ВЕРХНЕМ регистре (ПРИВЕТ, NATO), 2-буквенные коды (UA) и смешанный регистр (iPhone, CS2).
        /// </summary>
        public bool TryFixDoubleCapitals(string word, AppSettings settings, out string fixedWord)
        {
            fixedWord = word;
            if (settings == null || !settings.FixDoubleCapitals || string.IsNullOrEmpty(word) || word.Length < 3)
            {
                return false;
            }

            if (IsIgnored(word))
            {
                return false;
            }

            if (!char.IsLetter(word[0]) || !char.IsUpper(word[0])
                || !char.IsLetter(word[1]) || !char.IsUpper(word[1]))
            {
                return false;
            }

            // Все символы начиная с 3-го обязаны быть строчными буквами
            for (int i = 2; i < word.Length; i++)
            {
                if (!char.IsLetter(word[i]) || !char.IsLower(word[i]))
                {
                    return false;
                }
            }

            fixedWord = string.Concat(
                word.Substring(0, 1),
                word.Substring(1).ToLower(CultureInfo.CurrentCulture));
            return !string.Equals(fixedWord, word, StringComparison.Ordinal);
        }

        /// <summary>
        /// Приоритет 2: проверяет, нужно ли автоматически переключить раскладку для набранного слова.
        /// </summary>
        public AutoSwitchWordResult EvaluateAutoSwitch(
            string word,
            SupportedLanguage? activeWindowLanguage,
            AppSettings settings,
            LayoutConverter layoutConverter)
        {
            if (settings == null || !settings.AutoSwitchLayout || string.IsNullOrEmpty(word))
            {
                return new AutoSwitchWordResult(false, word, word, SupportedLanguage.English, SupportedLanguage.English);
            }

            if (!PassesAutoSwitchProtectionFilters(word) || IsIgnored(word))
            {
                return new AutoSwitchWordResult(false, word, word, SupportedLanguage.English, SupportedLanguage.English);
            }

            if (settings.IgnoreProfanityInTypoCorrection && _profanityFilter.IsProfanityOrSlang(word))
            {
                return new AutoSwitchWordResult(false, word, word, SupportedLanguage.English, SupportedLanguage.English);
            }

            settings.Normalize();

            SupportedLanguage sourceLanguage = activeWindowLanguage ?? layoutConverter.DetectSourceLanguage(word, settings);

            if (ContainsWord(sourceLanguage, word))
            {
                return new AutoSwitchWordResult(false, word, word, sourceLanguage, sourceLanguage);
            }

            SupportedLanguage detectedByScript = layoutConverter.DetectSourceLanguage(word, settings);
            if (detectedByScript != sourceLanguage && ContainsWord(detectedByScript, word))
            {
                return new AutoSwitchWordResult(false, word, word, detectedByScript, detectedByScript);
            }

            SupportedLanguage? bestTarget = null;
            string bestConverted = word;
            int bestRank = int.MaxValue;

            foreach (SupportedLanguage candidateLang in settings.TargetLanguages)
            {
                if (candidateLang == sourceLanguage)
                {
                    continue;
                }

                string converted = layoutConverter.ConvertText(word, sourceLanguage, candidateLang);
                if (string.Equals(converted, word, StringComparison.Ordinal))
                {
                    continue;
                }

                int rank = GetWordFrequencyRank(candidateLang, converted);
                if (rank < int.MaxValue && (bestTarget == null || rank < bestRank))
                {
                    bestTarget = candidateLang;
                    bestConverted = converted;
                    bestRank = rank;
                }
            }

            if (bestTarget.HasValue)
            {
                return new AutoSwitchWordResult(true, word, bestConverted, sourceLanguage, bestTarget.Value);
            }

            return new AutoSwitchWordResult(false, word, word, sourceLanguage, sourceLanguage);
        }

        /// <summary>
        /// Приоритет 3: взвешенный клавиатурный алгоритм исправления механических опечаток (до 2 ошибок для слов от 5 букв).
        /// Учитывает защиту ненормативной лексики, телеметрию скоростной печати и физическую матрицу клавиш.
        /// </summary>
        public TypoCorrectionResult EvaluateTypoCorrection(
            string word,
            SupportedLanguage activeLanguage,
            AppSettings settings,
            bool isBurstTyping = true)
        {
            if (settings == null || !settings.AutoCorrectTypos || string.IsNullOrEmpty(word))
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            if (word.Length < 3 || IsIgnored(word))
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            // Слова с заглавной буквы и цифрами не исправляем
            for (int i = 0; i < word.Length; i++)
            {
                if (char.IsUpper(word[i]) || char.IsDigit(word[i]))
                {
                    return new TypoCorrectionResult(false, word, word, activeLanguage);
                }
            }

            string lowerWord = NormalizeWord(word);
            if (string.IsNullOrEmpty(lowerWord) || lowerWord.Length < 3)
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            if (GamingAndTechTokens.Contains(lowerWord) || ContainsWord(activeLanguage, lowerWord))
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            // Защита мата и сленга от замены на обычные слова (например, поехать / проехать)
            if (settings.IgnoreProfanityInTypoCorrection && _profanityFilter.IsProfanityOrSlang(lowerWord))
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            if (!Alphabets.TryGetValue(activeLanguage, out string? alphabet))
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            foreach (char ch in lowerWord)
            {
                if (alphabet.IndexOf(ch) < 0)
                {
                    return new TypoCorrectionResult(false, word, word, activeLanguage);
                }
            }

            int m = lowerWord.Length;
            bool isShortWord = m < 5;
            double maxAllowedWeight = isShortWord
                ? MaxWeightShortWord
                : (isBurstTyping ? MaxWeightBurstTyping : MaxWeightNormalTyping);
            int maxLenDiff = isShortWord ? 1 : 2;

            bool[]? adjTable = FastAdjacencyTables.TryGetValue(activeLanguage, out bool[]? tbl) ? tbl : null;

            // Предварительно рассчитываем базовые стоимости удаления для каждой позиции s один раз
            Span<double> sBaseDelCosts = m <= 64 ? stackalloc double[m] : new double[m];
            for (int i = 0; i < m; i++)
            {
                char extra = lowerWord[i];
                bool nearNeighbor =
                    (i > 0 && (lowerWord[i - 1] == extra || AreKeysAdjacentFast(adjTable, lowerWord[i - 1], extra)))
                    || (i + 1 < m && (lowerWord[i + 1] == extra || AreKeysAdjacentFast(adjTable, lowerWord[i + 1], extra)));
                sBaseDelCosts[i] = nearNeighbor ? WeightAdjacentDoubleHitInsertion : WeightNonAdjacentEdit;
            }

            List<RankedWord> candidatePool = CollectCandidatePool(activeLanguage, lowerWord, maxLenDiff, adjTable);

            string? bestWord = null;
            double bestWeight = double.PositiveInfinity;
            int bestRank = int.MaxValue;
            double secondBestWeight = double.PositiveInfinity;
            int secondBestRank = int.MaxValue;

            foreach (RankedWord candidate in candidatePool)
            {
                string target = candidate.Word;
                int n = target.Length;

                if (Math.Abs(m - n) > maxLenDiff)
                {
                    continue;
                }

                // Быстрое отсечение по минимально возможной стоимости длины
                double minLenCost = n < m
                    ? (m - n) * WeightAdjacentDoubleHitInsertion
                    : (n - m) * WeightFastOmission;
                if (minLenCost > maxAllowedWeight + 0.0001)
                {
                    continue;
                }

                double cost = ComputeWeightedEditDistance(
                    lowerWord,
                    target,
                    candidate.Rank,
                    maxAllowedWeight,
                    adjTable,
                    sBaseDelCosts);

                if (cost > maxAllowedWeight + 0.0001)
                {
                    continue;
                }

                // Для коротких 3-буквенных слов разрешаем только высокочастотные слова
                if (m == 3 && candidate.Rank > 12000)
                {
                    continue;
                }

                // Несоседние одиночные ошибки (1.25) допускаем только для достаточно частых слов длиной >= 5
                if (cost >= WeightNonAdjacentEdit - 0.01 && (m < 5 || candidate.Rank > 12000))
                {
                    continue;
                }

                // Запрещаем предлагать матерные слова в качестве замены обычных слов (только для прошедших DP кандидатов)
                if (settings.IgnoreProfanityInTypoCorrection
                    && candidate.Rank > 0
                    && _profanityFilter.IsProfanityOrSlang(target))
                {
                    continue;
                }

                if (IsBetterCandidate(cost, candidate.Rank, bestWeight, bestRank))
                {
                    secondBestWeight = bestWeight;
                    secondBestRank = bestRank;
                    bestWeight = cost;
                    bestRank = candidate.Rank;
                    bestWord = target;
                }
                else if (IsBetterCandidate(cost, candidate.Rank, secondBestWeight, secondBestRank))
                {
                    secondBestWeight = cost;
                    secondBestRank = candidate.Rank;
                }
            }

            if (bestWord == null)
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            // При близком весе у двух кандидатов проверяем, есть ли явный частотный лидер
            if (!double.IsInfinity(secondBestWeight)
                && Math.Abs(bestWeight - secondBestWeight) < 0.05
                && bestRank > 5000
                && secondBestRank < bestRank * 2)
            {
                return new TypoCorrectionResult(false, word, word, activeLanguage);
            }

            return new TypoCorrectionResult(true, word, bestWord, activeLanguage, bestWeight);
        }

        private List<RankedWord> CollectCandidatePool(
            SupportedLanguage language,
            string typedWord,
            int maxLenDiff,
            bool[]? adjTable)
        {
            var pool = new List<RankedWord>(512);
            int minLen = Math.Max(3, typedWord.Length - maxLenDiff);
            int maxLen = typedWord.Length + maxLenDiff;

            char c0 = typedWord[0];
            char? c1 = typedWord.Length > 1 ? typedWord[1] : null;
            char? c2 = typedWord.Length > 2 ? typedWord[2] : null;

            bool IsPlausibleFirstChar(char targetFirst)
            {
                if (targetFirst == c0 || (c1.HasValue && targetFirst == c1.Value))
                {
                    return true;
                }

                if (maxLenDiff >= 2 && c2.HasValue && targetFirst == c2.Value)
                {
                    return true;
                }

                return AreKeysAdjacentFast(adjTable, c0, targetFirst);
            }

            lock (_lock)
            {
                // 1. Пользовательские слова всегда имеют наивысший приоритет (rank = 0)
                foreach (string userWord in _userWordSets[language])
                {
                    if (userWord.Length >= minLen && userWord.Length <= maxLen)
                    {
                        pool.Add(new RankedWord(userWord, 0));
                    }
                }

                // 2. Слова из встроенного частотного словаря, сгруппированные по длине
                Dictionary<int, List<RankedWord>> byLen = _builtInByLength[language];
                for (int len = minLen; len <= maxLen; len++)
                {
                    if (!byLen.TryGetValue(len, out List<RankedWord>? bucket))
                    {
                        continue;
                    }

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        RankedWord item = bucket[i];
                        // Быстрый предфильтр по первой букве или её соседям / соседней позиции (отсекает > 85% словаря)
                        if (IsPlausibleFirstChar(item.Word[0])
                            || (item.Word.Length > 1 && item.Word[1] == c0))
                        {
                            pool.Add(item);
                        }
                    }
                }
            }

            return pool;
        }

        /// <summary>
        /// Вычисляет взвешенное расстояние редактирования (Banded Early-Exit DP на stackalloc) от слова s к кандидату t.
        /// </summary>
        private static double ComputeWeightedEditDistance(
            string s,
            string t,
            int candidateRank,
            double maxWeight,
            bool[]? adjTable,
            ReadOnlySpan<double> sBaseDelCosts)
        {
            int m = s.Length;
            int n = t.Length;
            int stride = n + 1;
            int totalCells = (m + 1) * stride;
            const double inf = 1e9;

            Span<double> dp = totalCells <= 1400
                ? stackalloc double[totalCells]
                : new double[totalCells];
            dp.Fill(inf);

            dp[0] = 0.0;

            for (int i = 1; i <= m; i++)
            {
                dp[i * stride] = dp[(i - 1) * stride] + sBaseDelCosts[i - 1];
            }

            for (int j = 1; j <= n; j++)
            {
                dp[j] = dp[j - 1] + WeightFastOmission;
            }

            for (int i = 1; i <= m; i++)
            {
                double rowMin = inf;
                char sc = s[i - 1];
                double baseDel = sBaseDelCosts[i - 1];
                int rowOffset = i * stride;
                int prevRowOffset = (i - 1) * stride;
                int jMin = Math.Max(1, i - 3);
                int jMax = Math.Min(n, i + 3);

                for (int j = jMin; j <= jMax; j++)
                {
                    char tc = t[j - 1];

                    // 1. Совпадение или замена (Substitution)
                    double subCost = sc == tc
                        ? 0.0
                        : (AreKeysAdjacentFast(adjTable, sc, tc) ? WeightAdjacentSubstitution : WeightNonAdjacentEdit);

                    double best = dp[prevRowOffset + (j - 1)] + subCost;

                    // 2. Удаление лишней нажатой клавиши из s (Double-Hit Insertion в s)
                    double delCost = baseDel;
                    if (delCost > WeightAdjacentDoubleHitInsertion
                        && (sc == tc || AreKeysAdjacentFast(adjTable, sc, tc)))
                    {
                        delCost = WeightAdjacentDoubleHitInsertion;
                    }

                    double delCand = dp[prevRowOffset + j] + delCost;
                    if (delCand < best) best = delCand;

                    // 3. Пропуск буквы t[j-1] на высокой скорости (Omission)
                    double insCost = WeightFastOmission;
                    if (candidateRank > 10000
                        && j >= 2 && j < n
                        && (t[j] == 'ь' || t[j] == 'ъ')
                        && SlavicVowels.Contains(t[j - 2])
                        && !SlavicVowels.Contains(tc))
                    {
                        insCost += 0.65;
                    }

                    double insCand = dp[rowOffset + (j - 1)] + insCost;
                    if (insCand < best) best = insCand;

                    // 4. Перестановка двух соседних букв (Transposition)
                    if (i >= 2 && j >= 2 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1])
                    {
                        double transCand = dp[(i - 2) * stride + (j - 2)] + WeightTransposition;
                        if (transCand < best) best = transCand;
                    }

                    dp[rowOffset + j] = best;
                    if (best < rowMin)
                    {
                        rowMin = best;
                    }
                }

                if (rowMin > maxWeight + 0.0001)
                {
                    return inf;
                }
            }

            return dp[(m * stride) + n];
        }

        private static int MapCharToAdjacencySlot(char ch)
        {
            if (ch < 128)
            {
                return ch;
            }

            if (ch >= 0x0400 && ch < 0x0500)
            {
                return (ch - 0x0400) + 128;
            }

            return -1;
        }

        private static bool AreKeysAdjacentFast(bool[]? table, char a, char b)
        {
            if (table == null || a == b)
            {
                return false;
            }

            int idxA = MapCharToAdjacencySlot(a);
            int idxB = MapCharToAdjacencySlot(b);
            if (idxA < 0 || idxB < 0)
            {
                return false;
            }

            return table[(idxA * 384) + idxB];
        }

        private static Dictionary<SupportedLanguage, bool[]> BuildFastAdjacencyTables()
        {
            var tables = new Dictionary<SupportedLanguage, bool[]>();
            foreach (KeyValuePair<SupportedLanguage, Dictionary<char, (double Row, double Col)>> pair in KeyPositions)
            {
                var table = new bool[384 * 384];
                foreach (char a in pair.Value.Keys)
                {
                    int idxA = MapCharToAdjacencySlot(a);
                    if (idxA < 0) continue;

                    foreach (char b in pair.Value.Keys)
                    {
                        int idxB = MapCharToAdjacencySlot(b);
                        if (idxB < 0 || a == b) continue;

                        if (AreKeysAdjacent(pair.Value, a, b))
                        {
                            table[(idxA * 384) + idxB] = true;
                        }
                    }
                }

                tables[pair.Key] = table;
            }

            return tables;
        }

        private static bool IsBetterCandidate(double weightA, int rankA, double weightB, int rankB)
        {
            if (weightA < weightB - 0.0001)
            {
                return true;
            }

            if (Math.Abs(weightA - weightB) <= 0.0001 && rankA < rankB)
            {
                return true;
            }

            return false;
        }

        public static List<string> ParseBulkWordInput(string rawInput)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                return result;
            }

            char[] separators = { ',', ';', '\r', '\n', '\t', ' ' };
            string[] tokens = rawInput.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                string normalized = NormalizeWord(token);
                if (!string.IsNullOrEmpty(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        public static string NormalizeWord(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            return raw.Trim()
                .Replace('\u2019', '\'')
                .Replace('\u02BC', '\'')
                .ToLowerInvariant();
        }

        private int GetWordFrequencyRank(SupportedLanguage language, string word)
        {
            string normalized = NormalizeWord(word);
            if (string.IsNullOrEmpty(normalized))
            {
                return int.MaxValue;
            }

            lock (_lock)
            {
                if (_userWordSets[language].Contains(normalized))
                {
                    return 0;
                }

                if (_builtInRanks[language].TryGetValue(normalized, out int rank))
                {
                    return rank + 1;
                }
            }

            return int.MaxValue;
        }

        private void ApplyUserEntriesLocked(IEnumerable<UserDictionaryEntry> entries)
        {
            _userEntries.Clear();
            foreach (HashSet<string> set in _userWordSets.Values)
            {
                set.Clear();
            }

            foreach (UserDictionaryEntry entry in entries)
            {
                if (entry == null || !Enum.IsDefined(typeof(SupportedLanguage), entry.Language))
                {
                    continue;
                }

                string normalized = NormalizeWord(entry.Word);
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }

                if (_userWordSets[entry.Language].Add(normalized))
                {
                    _userEntries.Add(new UserDictionaryEntry
                    {
                        Word = normalized,
                        Language = entry.Language
                    });
                }
            }
        }

        private void SaveIgnoredWordsLocked()
        {
            List<string> snapshot = _ignoredWords.OrderBy(w => w, StringComparer.Ordinal).ToList();
            _settingsManager.SaveIgnoredWords(snapshot);
        }

        private void SaveUserDictionaryLocked()
        {
            List<UserDictionaryEntry> snapshot = _userEntries
                .OrderBy(e => e.Word, StringComparer.Ordinal)
                .ThenBy(e => e.Language)
                .ToList();
            _settingsManager.SaveUserDictionary(snapshot);
        }

        private static bool PassesAutoSwitchProtectionFilters(string word)
        {
            if (word.Length < 2)
            {
                return false;
            }

            if (GamingAndTechTokens.Contains(word))
            {
                return false;
            }

            string lower = word.ToLowerInvariant();
            if (lower.StartsWith("http", StringComparison.Ordinal)
                || lower.StartsWith("www.", StringComparison.Ordinal)
                || lower.Contains("://", StringComparison.Ordinal)
                || lower.EndsWith(".com", StringComparison.Ordinal)
                || lower.EndsWith(".ru", StringComparison.Ordinal)
                || lower.EndsWith(".ua", StringComparison.Ordinal)
                || lower.EndsWith(".net", StringComparison.Ordinal)
                || lower.EndsWith(".org", StringComparison.Ordinal))
            {
                return false;
            }

            int letterLikeCount = 0;
            foreach (char ch in word)
            {
                if (char.IsDigit(ch))
                {
                    return false;
                }

                if (ch == '@' || ch == '/' || ch == '_' || ch == '=' || ch == '+' || ch == '*'
                    || ch == '&' || ch == '#' || ch == '$' || ch == '%' || ch == '^' || ch == '(' || ch == ')')
                {
                    return false;
                }

                if (char.IsLetter(ch) || ch == '[' || ch == ']' || ch == '{' || ch == '}'
                    || ch == ';' || ch == ':' || ch == '\'' || ch == '"' || ch == '`' || ch == '~'
                    || ch == ',' || ch == '.' || ch == '<' || ch == '>' || ch == '\\' || ch == '|')
                {
                    letterLikeCount++;
                }
            }

            return letterLikeCount >= 2;
        }

        private static bool AreKeysAdjacent(
            Dictionary<char, (double Row, double Col)>? map,
            char a,
            char b)
        {
            if (map == null || a == b)
            {
                return false;
            }

            if (!map.TryGetValue(a, out (double Row, double Col) posA)
                || !map.TryGetValue(b, out (double Row, double Col) posB))
            {
                return false;
            }

            double dr = posA.Row - posB.Row;
            double dc = posA.Col - posB.Col;
            return (dr * dr + dc * dc) <= 2.15;
        }

        private static Dictionary<SupportedLanguage, Dictionary<char, (double Row, double Col)>> BuildKeyPositions()
        {
            string[] enRows =
            {
                "qwertyuiop[]",
                "asdfghjkl;'",
                "zxcvbnm,."
            };

            string[] ruRows =
            {
                "йцукенгшщзхъ",
                "фывапролджэ",
                "ячсмитьбю"
            };

            string[] ukRows =
            {
                "йцукенгшщзхї",
                "фівапролджє",
                "ячсмитьбю"
            };

            return new Dictionary<SupportedLanguage, Dictionary<char, (double Row, double Col)>>
            {
                [SupportedLanguage.English] = BuildGrid(enRows, ('`', 0.0, 0.0), ('\\', 1.0, 12.25)),
                [SupportedLanguage.Russian] = BuildGrid(ruRows, ('ё', 0.0, 0.0), ('\\', 1.0, 12.25)),
                [SupportedLanguage.Ukrainian] = BuildGrid(ukRows, ('\'', 0.0, 0.0), ('ґ', 1.0, 12.25))
            };
        }

        private static Dictionary<char, (double Row, double Col)> BuildGrid(
            string[] rows,
            params (char Char, double Row, double Col)[] extras)
        {
            double[] rowOffsets = { 0.25, 0.50, 1.00 };
            var map = new Dictionary<char, (double Row, double Col)>();

            for (int r = 0; r < rows.Length; r++)
            {
                string rowStr = rows[r];
                for (int c = 0; c < rowStr.Length; c++)
                {
                    map[rowStr[c]] = (r + 1.0, c + rowOffsets[r]);
                }
            }

            foreach ((char ch, double row, double col) in extras)
            {
                map[ch] = (row, col);
            }

            return map;
        }
    }
}
