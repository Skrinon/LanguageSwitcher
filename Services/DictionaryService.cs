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
        public string SourceLanguage { get; }
        public string TargetLanguage { get; }

        public AutoSwitchWordResult(
            bool shouldSwitch,
            string originalWord,
            string convertedWord,
            string sourceLanguage,
            string targetLanguage)
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
        public string Language { get; }
        public double ErrorWeight { get; }

        public TypoCorrectionResult(
            bool shouldCorrect,
            string originalWord,
            string correctedWord,
            string language,
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
    /// Управляет встроенными офлайн-словарями (Dictionaries/*.txt и EmbeddedResource),
    /// пользовательским словарём (user_dictionary.json для любого KLID мира),
    /// списком исключений (ignored_words.json), фильтром ненормативной лексики,
    /// исправлением двойных заглавных, автопереключением раскладки и динамическим клавиатурным исправлением опечаток.
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
        private const double MaxWeightShortWord = 0.75;    // Для слов 3-4 символа (до 1 ошибки)
        private const double MaxWeightNormalTyping = 1.15; // Обычный темп печати (>= 5 символов)
        private const double MaxWeightBurstTyping = 1.45;  // Скоростная печать (>= 5 символов)

        private const string EmbeddedDictPrefix = "LanguageSwitcher.Dictionaries.";
        private const string EmbeddedDictSuffix = ".txt";

        private readonly SettingsManager _settingsManager;
        private readonly ProfanityFilterService _profanityFilter;
        private readonly UniversalLayoutService _layoutService;
        private readonly string _dictionariesDirectory;
        private readonly bool _isDefaultDirectory;
        private readonly object _lock = new object();

        // Встроенные частотные словари: IsoLanguageCode (например "en", "ru", "uk", "de") -> (слово -> ранг)
        private readonly Dictionary<string, Dictionary<string, int>> _builtInRanks =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // Индексы встроенных словарей для мгновенного поиска по длине слова
        private readonly Dictionary<string, Dictionary<int, List<RankedWord>>> _builtInByLength =
            new Dictionary<string, Dictionary<int, List<RankedWord>>>(StringComparer.OrdinalIgnoreCase);

        // Пользовательские словари, привязанные к конкретному KLID раскладки ("00000409", "00000407" и т.д.)
        private readonly Dictionary<string, Dictionary<string, int>> _userWordRanksByKlid =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<int, List<RankedWord>>> _userByLengthByKlid =
            new Dictionary<string, Dictionary<int, List<RankedWord>>>(StringComparer.OrdinalIgnoreCase);

        private readonly List<UserDictionaryEntry> _userEntries = new List<UserDictionaryEntry>();
        private readonly HashSet<string> _ignoredWords = new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<string> GamingAndTechTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "wasd", "awp", "gg", "wp", "gl", "hf", "afk", "ez", "fps", "rpg", "npc",
            "lol", "omg", "idk", "brb", "btw", "gtg", "xd", "ctrl", "alt", "shift",
            "tab", "esc", "del", "http", "https", "www", "ftp", "ssh", "sql", "json",
            "xml", "html", "css", "api", "url", "exe", "dll", "pdf", "png", "jpg"
        };

        private static readonly HashSet<char> SlavicVowels = new HashSet<char>("аеёиоуыэюяіїє");

        public bool IsLoaded { get; private set; }

        public DictionaryService(
            SettingsManager settingsManager,
            ProfanityFilterService? profanityFilter = null,
            string? customDictionariesDirectory = null,
            UniversalLayoutService? layoutService = null)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _layoutService = layoutService ?? UniversalLayoutService.Instance;
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
            : this(settingsManager, new ProfanityFilterService(customDictionariesDirectory), customDictionariesDirectory, UniversalLayoutService.Instance)
        {
        }

        /// <summary>
        /// Асинхронно загружает все встроенные офлайн-словари (Dictionaries/*.txt и EmbeddedResource)
        /// и индексирует их по языковому коду и длине слов.
        /// </summary>
        public async Task LoadBuiltInDictionariesAsync()
        {
            var loaded = await Task.Run(() =>
            {
                var ranksResult = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                var byLenResult = new Dictionary<string, Dictionary<int, List<RankedWord>>>(StringComparer.OrdinalIgnoreCase);

                var sources = new Dictionary<string, Func<IEnumerable<string>>>(StringComparer.OrdinalIgnoreCase);

                // 1. Встроенные ресурсы внутри сборки (en.txt, ru.txt, uk.txt)
                if (_isDefaultDirectory)
                {
                    var assembly = typeof(DictionaryService).Assembly;
                    foreach (string resName in assembly.GetManifestResourceNames())
                    {
                        if (!resName.StartsWith(EmbeddedDictPrefix, StringComparison.OrdinalIgnoreCase)
                            || !resName.EndsWith(EmbeddedDictSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string fileNameNoExt = resName.Substring(
                            EmbeddedDictPrefix.Length,
                            resName.Length - EmbeddedDictPrefix.Length - EmbeddedDictSuffix.Length);
                        if (string.Equals(fileNameNoExt, "profanity_filter", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string langKey = NormalizeDictionaryLanguageKey(fileNameNoExt);
                        string capturedResName = resName;
                        sources[langKey] = () => ReadLinesFromEmbeddedResource(assembly, capturedResName);
                    }
                }

                // 2. Файлы Dictionaries/*.txt на диске (перекрывают или дополняют встроенные, например de.txt, fr.txt, pl.txt)
                if (Directory.Exists(_dictionariesDirectory))
                {
                    foreach (string file in Directory.GetFiles(_dictionariesDirectory, "*.txt"))
                    {
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                        if (string.Equals(fileNameNoExt, "profanity_filter", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string langKey = NormalizeDictionaryLanguageKey(fileNameNoExt);
                        string capturedFile = file;
                        sources[langKey] = () => File.ReadLines(capturedFile, Encoding.UTF8);
                    }
                }

                foreach (KeyValuePair<string, Func<IEnumerable<string>>> source in sources)
                {
                    var map = new Dictionary<string, int>(StringComparer.Ordinal);
                    var byLen = new Dictionary<int, List<RankedWord>>();
                    IndexDictionaryLines(source.Value(), map, byLen);
                    ranksResult[source.Key] = map;
                    byLenResult[source.Key] = byLen;
                }

                return (ranksResult, byLenResult);
            }).ConfigureAwait(false);

            lock (_lock)
            {
                _builtInRanks.Clear();
                _builtInByLength.Clear();

                foreach (KeyValuePair<string, Dictionary<string, int>> pair in loaded.ranksResult)
                {
                    _builtInRanks[pair.Key] = pair.Value;
                }

                foreach (KeyValuePair<string, Dictionary<int, List<RankedWord>>> pair in loaded.byLenResult)
                {
                    _builtInByLength[pair.Key] = pair.Value;
                }

                IsLoaded = true;
            }
        }

        private string NormalizeDictionaryLanguageKey(string rawKey)
        {
            string trimmed = rawKey.Trim();
            if (trimmed.Length == 8 && uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return trimmed.ToUpperInvariant();
            }

            if (trimmed.Length == 2 || trimmed.Length == 3)
            {
                return trimmed.ToLowerInvariant();
            }

            KeyboardLayoutInfo info = _layoutService.GetLayoutInfo(trimmed);
            return info.IsoLanguageCode;
        }

        private static IEnumerable<string> ReadLinesFromEmbeddedResource(System.Reflection.Assembly assembly, string resourceName)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                yield break;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
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

        /// <summary>
        /// Возвращает true, если для указанного KLID (или его языкового семейства ISO) загружен встроенный или пользовательский словарь.
        /// </summary>
        public bool HasDictionary(string klidOrAlias)
        {
            string klid = _layoutService.NormalizeKlid(klidOrAlias);
            string iso = _layoutService.GetLayoutInfo(klid).IsoLanguageCode;

            lock (_lock)
            {
                return HasDictionaryLocked(klid, iso);
            }
        }

        /// <summary>
        /// Возвращает суммарное количество слов (встроенных + пользовательских) для указанного языка/KLID.
        /// </summary>
        public int GetWordCountForLanguage(string klidOrAlias)
        {
            string klid = _layoutService.NormalizeKlid(klidOrAlias);
            string iso = _layoutService.GetLayoutInfo(klid).IsoLanguageCode;

            lock (_lock)
            {
                int total = 0;
                if (_userWordRanksByKlid.TryGetValue(klid, out Dictionary<string, int>? userRanks))
                {
                    total += userRanks.Count;
                }

                if (_builtInRanks.TryGetValue(klid, out Dictionary<string, int>? klidBuiltIn))
                {
                    total += klidBuiltIn.Count;
                }
                else if (_builtInRanks.TryGetValue(iso, out Dictionary<string, int>? isoBuiltIn))
                {
                    total += isoBuiltIn.Count;
                }

                return total;
            }
        }

        private bool HasDictionaryLocked(string klid, string iso)
        {
            if (_userWordRanksByKlid.TryGetValue(klid, out Dictionary<string, int>? userRanks) && userRanks.Count > 0)
            {
                return true;
            }

            if (_builtInRanks.TryGetValue(klid, out Dictionary<string, int>? klidBuiltIn) && klidBuiltIn.Count > 0)
            {
                return true;
            }

            return _builtInRanks.TryGetValue(iso, out Dictionary<string, int>? isoBuiltIn) && isoBuiltIn.Count > 0;
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

        public List<string> GetUserDictionaryLanguagesSnapshot()
        {
            lock (_lock)
            {
                return _userWordRanksByKlid
                    .Where(kv => kv.Value.Count > 0)
                    .Select(kv => kv.Key)
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
            string languageKlidOrAlias)
        {
            string klid = _layoutService.NormalizeKlid(languageKlidOrAlias);
            int added = 0;
            int skipped = 0;

            lock (_lock)
            {
                EnsureUserBucketLocked(klid, out Dictionary<string, int> ranks, out Dictionary<int, List<RankedWord>> byLen);

                foreach (string raw in words)
                {
                    string normalized = NormalizeWord(raw);
                    if (string.IsNullOrEmpty(normalized))
                    {
                        continue;
                    }

                    if (!ranks.ContainsKey(normalized))
                    {
                        int rank = ranks.Count;
                        ranks[normalized] = rank;
                        AddWordToLengthBucket(byLen, normalized, rank);

                        _userEntries.Add(new UserDictionaryEntry
                        {
                            Word = normalized,
                            Language = klid
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

        public bool RemoveUserWord(string word, string languageKlidOrAlias)
        {
            string klid = _layoutService.NormalizeKlid(languageKlidOrAlias);
            string normalized = NormalizeWord(word);

            lock (_lock)
            {
                if (_userWordRanksByKlid.TryGetValue(klid, out Dictionary<string, int>? ranks) && ranks.Remove(normalized))
                {
                    _userEntries.RemoveAll(e => string.Equals(e.Language, klid, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.Word, normalized, StringComparison.Ordinal));
                    RebuildUserLengthIndexLocked(klid);
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
                _userWordRanksByKlid.Clear();
                _userByLengthByKlid.Clear();
                SaveUserDictionaryLocked();
            }
        }

        public bool ContainsWord(string klidOrAlias, string word)
        {
            string normalized = NormalizeWord(word);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            string klid = _layoutService.NormalizeKlid(klidOrAlias);
            string iso = _layoutService.GetLayoutInfo(klid).IsoLanguageCode;

            lock (_lock)
            {
                if (_userWordRanksByKlid.TryGetValue(klid, out Dictionary<string, int>? userRanks)
                    && userRanks.ContainsKey(normalized))
                {
                    return true;
                }

                if (_builtInRanks.TryGetValue(klid, out Dictionary<string, int>? klidBuiltIn)
                    && klidBuiltIn.ContainsKey(normalized))
                {
                    return true;
                }

                if (_builtInRanks.TryGetValue(iso, out Dictionary<string, int>? isoBuiltIn)
                    && isoBuiltIn.ContainsKey(normalized))
                {
                    return true;
                }

                return false;
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
        /// Проверяет только те активные целевые языки, у которых подключён встроенный или пользовательский словарь.
        /// </summary>
        public AutoSwitchWordResult EvaluateAutoSwitch(
            string word,
            string? activeWindowLanguage,
            AppSettings settings,
            LayoutConverter layoutConverter)
        {
            string fallbackLang = settings != null
                ? _layoutService.NormalizeKlid(settings.DefaultSourceLanguage)
                : UniversalLayoutService.EnglishUsKlid;

            if (settings == null || !settings.AutoSwitchLayout || string.IsNullOrEmpty(word))
            {
                return new AutoSwitchWordResult(false, word, word, fallbackLang, fallbackLang);
            }

            if (!PassesAutoSwitchProtectionFilters(word) || IsIgnored(word))
            {
                return new AutoSwitchWordResult(false, word, word, fallbackLang, fallbackLang);
            }

            if (settings.IgnoreProfanityInTypoCorrection && _profanityFilter.IsProfanityOrSlang(word))
            {
                return new AutoSwitchWordResult(false, word, word, fallbackLang, fallbackLang);
            }

            settings.Normalize();

            string sourceLanguage = !string.IsNullOrWhiteSpace(activeWindowLanguage)
                ? _layoutService.NormalizeKlid(activeWindowLanguage)
                : layoutConverter.DetectSourceLanguage(word, settings);

            if (ContainsWord(sourceLanguage, word))
            {
                return new AutoSwitchWordResult(false, word, word, sourceLanguage, sourceLanguage);
            }

            string detectedByScript = layoutConverter.DetectSourceLanguage(word, settings, sourceLanguage);
            if (!string.Equals(detectedByScript, sourceLanguage, StringComparison.OrdinalIgnoreCase)
                && ContainsWord(detectedByScript, word))
            {
                return new AutoSwitchWordResult(false, word, word, detectedByScript, detectedByScript);
            }

            string? bestTarget = null;
            string bestConverted = word;
            int bestRank = int.MaxValue;

            foreach (string candidateKlid in settings.TargetLanguages)
            {
                if (string.Equals(candidateKlid, sourceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Языки без словаря безопасно пропускаем в автоматическом режиме
                if (!HasDictionary(candidateKlid))
                {
                    continue;
                }

                string converted = layoutConverter.ConvertText(word, sourceLanguage, candidateKlid);
                if (string.Equals(converted, word, StringComparison.Ordinal))
                {
                    continue;
                }

                int rank = GetWordFrequencyRank(candidateKlid, converted);
                if (rank < int.MaxValue && (bestTarget == null || rank < bestRank))
                {
                    bestTarget = candidateKlid;
                    bestConverted = converted;
                    bestRank = rank;
                }
            }

            if (bestTarget != null)
            {
                return new AutoSwitchWordResult(true, word, bestConverted, sourceLanguage, bestTarget);
            }

            return new AutoSwitchWordResult(false, word, word, sourceLanguage, sourceLanguage);
        }

        /// <summary>
        /// Приоритет 3: взвешенный клавиатурный алгоритм исправления механических опечаток для любого языка со словарём.
        /// Учитывает защиту ненормативной лексики, телеметрию скоростной печати и динамическую матрицу соседних клавиш из ToUnicodeEx.
        /// </summary>
        public TypoCorrectionResult EvaluateTypoCorrection(
            string word,
            string activeLanguageKlid,
            AppSettings settings,
            bool isBurstTyping = true)
        {
            string activeKlid = _layoutService.NormalizeKlid(activeLanguageKlid);

            if (settings == null || !settings.AutoCorrectTypos || string.IsNullOrEmpty(word))
            {
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            if (word.Length < 3 || IsIgnored(word) || !HasDictionary(activeKlid))
            {
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            // Слова с заглавной буквы и цифрами не исправляем
            for (int i = 0; i < word.Length; i++)
            {
                if (char.IsUpper(word[i]) || char.IsDigit(word[i]))
                {
                    return new TypoCorrectionResult(false, word, word, activeKlid);
                }
            }

            string lowerWord = NormalizeWord(word);
            if (string.IsNullOrEmpty(lowerWord) || lowerWord.Length < 3)
            {
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            if (GamingAndTechTokens.Contains(lowerWord) || ContainsWord(activeKlid, lowerWord))
            {
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            // Защита мата и сленга от замены на обычные слова
            if (settings.IgnoreProfanityInTypoCorrection && _profanityFilter.IsProfanityOrSlang(lowerWord))
            {
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            DynamicLayoutProfile profile = _layoutService.GetProfile(activeKlid);
            bool hasLatinAlphabet = profile.AlphabetChars.Contains('a') && profile.AlphabetChars.Contains('e');

            foreach (char ch in lowerWord)
            {
                bool isAllowedChar = profile.AlphabetChars.Contains(ch)
                    || (hasLatinAlphabet && ch >= '\u00C0' && ch <= '\u024F');
                if (!isAllowedChar)
                {
                    return new TypoCorrectionResult(false, word, word, activeKlid);
                }
            }

            int m = lowerWord.Length;
            bool isShortWord = m < 5;
            double maxAllowedWeight = isShortWord
                ? MaxWeightShortWord
                : (isBurstTyping ? MaxWeightBurstTyping : MaxWeightNormalTyping);
            int maxLenDiff = isShortWord ? 1 : 2;

            // Предварительно рассчитываем базовые стоимости удаления для каждой позиции s один раз
            Span<double> sBaseDelCosts = m <= 64 ? stackalloc double[m] : new double[m];
            for (int i = 0; i < m; i++)
            {
                char extra = lowerWord[i];
                bool nearNeighbor =
                    (i > 0 && (lowerWord[i - 1] == extra || profile.AreKeysAdjacent(lowerWord[i - 1], extra)))
                    || (i + 1 < m && (lowerWord[i + 1] == extra || profile.AreKeysAdjacent(lowerWord[i + 1], extra)));
                sBaseDelCosts[i] = nearNeighbor ? WeightAdjacentDoubleHitInsertion : WeightNonAdjacentEdit;
            }

            List<RankedWord> candidatePool = CollectCandidatePool(activeKlid, profile.Info.IsoLanguageCode, lowerWord, maxLenDiff, profile);

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
                    profile,
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

                // Запрещаем предлагать матерные слова в качестве замены обычных слов
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
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            // При близком весе у двух кандидатов проверяем, есть ли явный частотный лидер
            if (!double.IsInfinity(secondBestWeight)
                && Math.Abs(bestWeight - secondBestWeight) < 0.05
                && bestRank > 5000
                && secondBestRank < bestRank * 2)
            {
                return new TypoCorrectionResult(false, word, word, activeKlid);
            }

            return new TypoCorrectionResult(true, word, bestWord, activeKlid, bestWeight);
        }

        private List<RankedWord> CollectCandidatePool(
            string klid,
            string isoCode,
            string typedWord,
            int maxLenDiff,
            DynamicLayoutProfile profile)
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

                return profile.AreKeysAdjacent(c0, targetFirst);
            }

            void AppendBucket(Dictionary<int, List<RankedWord>>? byLen)
            {
                if (byLen == null)
                {
                    return;
                }

                for (int len = minLen; len <= maxLen; len++)
                {
                    if (!byLen.TryGetValue(len, out List<RankedWord>? bucket))
                    {
                        continue;
                    }

                    for (int i = 0; i < bucket.Count; i++)
                    {
                        RankedWord item = bucket[i];
                        if (IsPlausibleFirstChar(item.Word[0])
                            || (item.Word.Length > 1 && item.Word[1] == c0))
                        {
                            pool.Add(item);
                        }
                    }
                }
            }

            lock (_lock)
            {
                // 1. Пользовательский словарь для данного KLID (от индекса по длине, чтобы миллионы/десятки тысяч импортированных слов работали за ~2 мс)
                if (_userByLengthByKlid.TryGetValue(klid, out Dictionary<int, List<RankedWord>>? userByLen))
                {
                    AppendBucket(userByLen);
                }

                // 2. Встроенный частотный словарь (сначала точный KLID если задан, иначе по IsoLanguageCode)
                if (_builtInByLength.TryGetValue(klid, out Dictionary<int, List<RankedWord>>? builtInExact))
                {
                    AppendBucket(builtInExact);
                }
                else if (_builtInByLength.TryGetValue(isoCode, out Dictionary<int, List<RankedWord>>? builtInIso))
                {
                    AppendBucket(builtInIso);
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
            DynamicLayoutProfile profile,
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
                        : (profile.AreKeysAdjacent(sc, tc) ? WeightAdjacentSubstitution : WeightNonAdjacentEdit);

                    double best = dp[prevRowOffset + (j - 1)] + subCost;

                    // 2. Удаление лишней нажатой клавиши из s (Double-Hit Insertion в s)
                    double delCost = baseDel;
                    if (delCost > WeightAdjacentDoubleHitInsertion
                        && (sc == tc || profile.AreKeysAdjacent(sc, tc)))
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

        private int GetWordFrequencyRank(string klidOrAlias, string word)
        {
            string normalized = NormalizeWord(word);
            if (string.IsNullOrEmpty(normalized))
            {
                return int.MaxValue;
            }

            string klid = _layoutService.NormalizeKlid(klidOrAlias);
            string iso = _layoutService.GetLayoutInfo(klid).IsoLanguageCode;

            lock (_lock)
            {
                if (_userWordRanksByKlid.TryGetValue(klid, out Dictionary<string, int>? userRanks)
                    && userRanks.TryGetValue(normalized, out int userRank))
                {
                    return userRank;
                }

                if (_builtInRanks.TryGetValue(klid, out Dictionary<string, int>? klidBuiltIn)
                    && klidBuiltIn.TryGetValue(normalized, out int klidRank))
                {
                    return klidRank + 1;
                }

                if (_builtInRanks.TryGetValue(iso, out Dictionary<string, int>? isoBuiltIn)
                    && isoBuiltIn.TryGetValue(normalized, out int isoRank))
                {
                    return isoRank + 1;
                }
            }

            return int.MaxValue;
        }

        private void EnsureUserBucketLocked(
            string klid,
            out Dictionary<string, int> ranks,
            out Dictionary<int, List<RankedWord>> byLen)
        {
            if (!_userWordRanksByKlid.TryGetValue(klid, out ranks!))
            {
                ranks = new Dictionary<string, int>(StringComparer.Ordinal);
                _userWordRanksByKlid[klid] = ranks;
            }

            if (!_userByLengthByKlid.TryGetValue(klid, out byLen!))
            {
                byLen = new Dictionary<int, List<RankedWord>>();
                _userByLengthByKlid[klid] = byLen;
            }
        }

        private static void AddWordToLengthBucket(Dictionary<int, List<RankedWord>> byLen, string word, int rank)
        {
            if (!byLen.TryGetValue(word.Length, out List<RankedWord>? bucket))
            {
                bucket = new List<RankedWord>();
                byLen[word.Length] = bucket;
            }

            bucket.Add(new RankedWord(word, rank));
        }

        private void RebuildUserLengthIndexLocked(string klid)
        {
            if (!_userWordRanksByKlid.TryGetValue(klid, out Dictionary<string, int>? ranks))
            {
                _userByLengthByKlid.Remove(klid);
                return;
            }

            var byLen = new Dictionary<int, List<RankedWord>>();
            foreach (KeyValuePair<string, int> kv in ranks)
            {
                AddWordToLengthBucket(byLen, kv.Key, kv.Value);
            }

            _userByLengthByKlid[klid] = byLen;
        }

        private void ApplyUserEntriesLocked(IEnumerable<UserDictionaryEntry> entries)
        {
            _userEntries.Clear();
            _userWordRanksByKlid.Clear();
            _userByLengthByKlid.Clear();

            foreach (UserDictionaryEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Word))
                {
                    continue;
                }

                string klid = _layoutService.NormalizeKlid(entry.Language);
                string normalized = NormalizeWord(entry.Word);
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }

                EnsureUserBucketLocked(klid, out Dictionary<string, int> ranks, out Dictionary<int, List<RankedWord>> byLen);
                if (!ranks.ContainsKey(normalized))
                {
                    int rank = ranks.Count;
                    ranks[normalized] = rank;
                    AddWordToLengthBucket(byLen, normalized, rank);
                    _userEntries.Add(new UserDictionaryEntry
                    {
                        Word = normalized,
                        Language = klid
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
                .ThenBy(e => e.Language, StringComparer.OrdinalIgnoreCase)
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
    }
}
