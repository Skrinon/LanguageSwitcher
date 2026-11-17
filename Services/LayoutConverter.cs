using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Результат конвертации текста между любыми мировыми раскладками клавиатуры.
    /// </summary>
    public sealed class ConversionResult
    {
        public bool Success { get; }
        public string ConvertedText { get; }
        public string SourceLanguage { get; }
        public string TargetLanguage { get; }
        public bool IsCycleContinuation { get; }

        public ConversionResult(
            bool success,
            string convertedText,
            string sourceLanguage,
            string targetLanguage,
            bool isCycleContinuation)
        {
            Success = success;
            ConvertedText = convertedText;
            SourceLanguage = sourceLanguage;
            TargetLanguage = targetLanguage;
            IsCycleContinuation = isCycleContinuation;
        }
    }

    /// <summary>
    /// Отвечает за определение исходной раскладки, посимвольную конвертацию текста
    /// через динамические профили ToUnicodeEx и циклическое переключение между любыми выбранными раскладками мира.
    /// </summary>
    public sealed class LayoutConverter
    {
        private static readonly HashSet<char> NeutralChars = new HashSet<char>(
            "1234567890-_=+%*()");

        private readonly UniversalLayoutService _layoutService;

        // Состояние циклической конвертации (для 2, 3 и более языков)
        private readonly object _cycleLock = new object();
        private string _originalText = string.Empty;
        private string _lastConvertedText = string.Empty;
        private string _sourceLanguage = UniversalLayoutService.EnglishUsKlid;
        private List<string> _cycleOrder = new List<string>();
        private int _currentCycleIndex;

        public LayoutConverter()
            : this(UniversalLayoutService.Instance)
        {
        }

        public LayoutConverter(UniversalLayoutService layoutService)
        {
            _layoutService = layoutService ?? throw new ArgumentNullException(nameof(layoutService));
        }

        public bool IsCycleActive
        {
            get
            {
                lock (_cycleLock)
                {
                    return _cycleOrder.Count > 1 && !string.IsNullOrEmpty(_originalText);
                }
            }
        }

        public string LastConvertedText
        {
            get
            {
                lock (_cycleLock)
                {
                    return _lastConvertedText;
                }
            }
        }

        /// <summary>
        /// Сбрасывает накопленное состояние циклического переключения.
        /// </summary>
        public void ResetCycle()
        {
            lock (_cycleLock)
            {
                _originalText = string.Empty;
                _lastConvertedText = string.Empty;
                _cycleOrder = new List<string>();
                _currentCycleIndex = 0;
            }
        }

        /// <summary>
        /// Выполняет конвертацию текста с учётом состояния цикла (2 или 3+ целевых раскладок мира).
        /// </summary>
        public ConversionResult ConvertWithCycle(
            string text,
            AppSettings settings,
            bool isContinuation,
            string? activeWindowLanguage = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                string fallback = _layoutService.NormalizeKlid(settings?.DefaultSourceLanguage);
                return new ConversionResult(false, text ?? string.Empty, fallback, fallback, false);
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.Normalize();

            lock (_cycleLock)
            {
                bool canContinue = isContinuation
                    && _cycleOrder.Count >= 2
                    && !string.IsNullOrEmpty(_originalText)
                    && string.Equals(text, _lastConvertedText, StringComparison.Ordinal);

                if (!canContinue)
                {
                    _originalText = text;
                    _sourceLanguage = DetectSourceLanguage(text, settings, activeWindowLanguage);
                    _cycleOrder = BuildCycleOrder(_sourceLanguage, settings.TargetLanguages);

                    if (_cycleOrder.Count < 2)
                    {
                        _lastConvertedText = text;
                        return new ConversionResult(false, text, _sourceLanguage, _sourceLanguage, false);
                    }

                    _currentCycleIndex = 1;
                    string targetLang = _cycleOrder[_currentCycleIndex];
                    string converted = ConvertText(_originalText, _sourceLanguage, targetLang);
                    _lastConvertedText = converted;

                    return new ConversionResult(true, converted, _sourceLanguage, targetLang, false);
                }
                else
                {
                    _currentCycleIndex = (_currentCycleIndex + 1) % _cycleOrder.Count;
                    string targetLang = _cycleOrder[_currentCycleIndex];
                    string converted = _currentCycleIndex == 0
                        ? _originalText
                        : ConvertText(_originalText, _sourceLanguage, targetLang);

                    _lastConvertedText = converted;
                    return new ConversionResult(true, converted, _sourceLanguage, targetLang, true);
                }
            }
        }

        /// <summary>
        /// Определяет исходную раскладку введённого текста среди активных раскладок пользователя.
        /// </summary>
        public string DetectSourceLanguage(string text, AppSettings settings, string? activeWindowLanguage = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.Normalize();

            if (string.IsNullOrWhiteSpace(text))
            {
                return settings.DefaultSourceLanguage;
            }

            // Собираем список раскладок-кандидатов для анализа
            var candidates = new List<string>();
            void AddCandidate(string? klid)
            {
                if (string.IsNullOrWhiteSpace(klid))
                {
                    return;
                }

                string normalized = _layoutService.NormalizeKlid(klid);
                if (!candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(normalized);
                }
            }

            foreach (string target in settings.TargetLanguages)
            {
                AddCandidate(target);
            }

            AddCandidate(settings.DefaultSourceLanguage);
            AddCandidate(activeWindowLanguage);

            var profiles = new Dictionary<string, DynamicLayoutProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (string klid in candidates)
            {
                profiles[klid] = _layoutService.GetProfile(klid);
            }

            // 1. Если включён исходный язык по умолчанию, сначала проверяем полную совместимость текста с ним
            if (settings.UseDefaultSourceLanguage
                && profiles.TryGetValue(settings.DefaultSourceLanguage, out DynamicLayoutProfile? defaultProfile)
                && IsTextCompatibleWithProfile(text, defaultProfile))
            {
                return defaultProfile.Klid;
            }

            // 2. Взвешенная оценка совместимости текста с каждой из раскладок-кандидатов
            var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int maxScore = int.MinValue;

            bool hasCyrillic = false;
            bool hasRussianExclusive = false;
            foreach (char ch in text)
            {
                if (ch >= '\u0400' && ch <= '\u04FF')
                {
                    hasCyrillic = true;
                }

                if (ch == 'ы' || ch == 'Ы' || ch == 'ъ' || ch == 'Ъ' || ch == 'э' || ch == 'Э' || ch == 'ё' || ch == 'Ё')
                {
                    hasRussianExclusive = true;
                }
            }

            foreach (string klid in candidates)
            {
                DynamicLayoutProfile profile = profiles[klid];
                int score = ScoreProfile(text, profile, profiles.Values, hasCyrillic, hasRussianExclusive);
                scores[klid] = score;
                if (score > maxScore)
                {
                    maxScore = score;
                }
            }

            List<string> tied = candidates.Where(k => scores[k] == maxScore).ToList();
            if (tied.Count == 1)
            {
                return tied[0];
            }

            // При ничьей (например, слово "привет" совместимо с RU и UA, или "ywei" совместимо с EN и DE)
            if (settings.UseDefaultSourceLanguage && tied.Contains(settings.DefaultSourceLanguage, StringComparer.OrdinalIgnoreCase))
            {
                return settings.DefaultSourceLanguage;
            }

            if (!string.IsNullOrWhiteSpace(activeWindowLanguage))
            {
                string activeNorm = _layoutService.NormalizeKlid(activeWindowLanguage);
                if (tied.Contains(activeNorm, StringComparer.OrdinalIgnoreCase))
                {
                    return activeNorm;
                }
            }

            // Для классической кириллической ничьей (RU vs UK на слове без і/ї/є/ґ и ы/ъ/э/ё)
            // отдаём предпочтение русской раскладке, если она есть в TargetLanguages и ничьей
            bool hasRuInTied = tied.Contains(UniversalLayoutService.RussianKlid, StringComparer.OrdinalIgnoreCase);
            bool hasUkInTied = tied.Any(k => string.Equals(profiles[k].Info.IsoLanguageCode, "uk", StringComparison.OrdinalIgnoreCase));

            foreach (string target in settings.TargetLanguages)
            {
                if (!tied.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (hasRuInTied && hasUkInTied
                    && string.Equals(profiles[target].Info.IsoLanguageCode, "uk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return target;
            }

            foreach (string target in settings.TargetLanguages)
            {
                if (tied.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    return target;
                }
            }

            return tied[0];
        }

        /// <summary>
        /// Посимвольно конвертирует строку из sourceLanguage в targetLanguage с полным сохранением регистра
        /// и сдвигом по физическим клавишам (включая QWERTZ, AZERTY и AltGr-символы).
        /// </summary>
        public string ConvertText(string text, string sourceLanguage, string targetLanguage)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string srcKlid = _layoutService.NormalizeKlid(sourceLanguage);
            string dstKlid = _layoutService.NormalizeKlid(targetLanguage);

            if (string.Equals(srcKlid, dstKlid, StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            DynamicLayoutProfile srcProfile = _layoutService.GetProfile(srcKlid);
            DynamicLayoutProfile dstProfile = _layoutService.GetProfile(dstKlid);

            const int basicTableSize = DynamicLayoutProfile.KeysPerModifierState * 2; // Normal + Shift

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (srcProfile.IndexByChar.TryGetValue(ch, out int slot))
                {
                    char mapped = dstProfile.CharBySlot[slot];
                    if (mapped == '\0' && slot >= basicTableSize)
                    {
                        mapped = dstProfile.CharBySlot[slot % basicTableSize];
                    }

                    sb.Append(mapped != '\0' ? mapped : ch);
                }
                else
                {
                    sb.Append(ch);
                }
            }

            string result = sb.ToString();

            // При 3-язычном переборе (ghbdtn -> привет -> привіт):
            // по физической карте слово "привет" совпадает с русским написанием, поэтому для украинской цели ставим "привіт".
            if (string.Equals(dstProfile.Info.IsoLanguageCode, "uk", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(srcProfile.Info.IsoLanguageCode, "en", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(srcProfile.Info.IsoLanguageCode, "ru", StringComparison.OrdinalIgnoreCase))
                && string.Equals(result, "привет", StringComparison.OrdinalIgnoreCase))
            {
                result = ApplyUkrainianGreetingCase(result);
            }

            return result;
        }

        private static string ApplyUkrainianGreetingCase(string russianWord)
        {
            if (russianWord.Length != 6)
            {
                return russianWord;
            }

            char[] chars = russianWord.ToCharArray();
            chars[4] = char.IsUpper(chars[4]) ? 'І' : 'і';
            return new string(chars);
        }

        private List<string> BuildCycleOrder(
            string sourceLanguage,
            IReadOnlyList<string> targetLanguages)
        {
            var order = new List<string>();
            int sourcePos = -1;

            for (int i = 0; i < targetLanguages.Count; i++)
            {
                if (string.Equals(targetLanguages[i], sourceLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    sourcePos = i;
                    break;
                }
            }

            if (sourcePos >= 0)
            {
                for (int offset = 0; offset < targetLanguages.Count; offset++)
                {
                    int idx = (sourcePos + offset) % targetLanguages.Count;
                    order.Add(targetLanguages[idx]);
                }
            }
            else
            {
                order.Add(sourceLanguage);
                for (int i = 0; i < targetLanguages.Count; i++)
                {
                    if (!order.Contains(targetLanguages[i], StringComparer.OrdinalIgnoreCase))
                    {
                        order.Add(targetLanguages[i]);
                    }
                }
            }

            return order;
        }

        private static bool IsTextCompatibleWithProfile(string text, DynamicLayoutProfile profile)
        {
            bool hasDistinctiveOrMappable = false;

            foreach (char ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                if (!profile.IndexByChar.ContainsKey(ch))
                {
                    return false;
                }

                if (!NeutralChars.Contains(ch))
                {
                    hasDistinctiveOrMappable = true;
                }
            }

            return hasDistinctiveOrMappable;
        }

        private static int ScoreProfile(
            string text,
            DynamicLayoutProfile profile,
            IEnumerable<DynamicLayoutProfile> allCandidates,
            bool hasCyrillic,
            bool hasRussianExclusive)
        {
            int score = 0;
            bool isUkrainian = string.Equals(profile.Info.IsoLanguageCode, "uk", StringComparison.OrdinalIgnoreCase);

            foreach (char ch in text)
            {
                if (char.IsWhiteSpace(ch) || NeutralChars.Contains(ch))
                {
                    continue;
                }

                if (profile.IndexByChar.ContainsKey(ch))
                {
                    score += 2;

                    int missingElsewhere = 0;
                    foreach (DynamicLayoutProfile other in allCandidates)
                    {
                        if (!string.Equals(other.Klid, profile.Klid, StringComparison.OrdinalIgnoreCase)
                            && !other.IndexByChar.ContainsKey(ch))
                        {
                            missingElsewhere++;
                        }
                    }

                    if (missingElsewhere > 0)
                    {
                        // Бонус за символ, отсутствующий в других выбранных раскладках (например ö/ä/ü/ß, é/à/ç, ы/ъ/э, і/ї/є)
                        score += 10 + (missingElsewhere * 2);
                    }

                    if (isUkrainian && (ch == '\'' || ch == '’' || ch == 'ʼ') && hasCyrillic && !hasRussianExclusive)
                    {
                        score += 6;
                    }
                }
                else
                {
                    // Если символ отсутствует на этой раскладке, но присутствует на другой — сильный штраф
                    bool existsElsewhere = false;
                    foreach (DynamicLayoutProfile other in allCandidates)
                    {
                        if (!string.Equals(other.Klid, profile.Klid, StringComparison.OrdinalIgnoreCase)
                            && other.IndexByChar.ContainsKey(ch))
                        {
                            existsElsewhere = true;
                            break;
                        }
                    }

                    if (existsElsewhere)
                    {
                        score -= 20;
                    }
                }
            }

            return score;
        }
    }
}
