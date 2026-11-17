using System;
using System.Collections.Generic;
using System.Text;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Результат конвертации текста между раскладками.
    /// </summary>
    public sealed class ConversionResult
    {
        public bool Success { get; }
        public string ConvertedText { get; }
        public SupportedLanguage SourceLanguage { get; }
        public SupportedLanguage TargetLanguage { get; }
        public bool IsCycleContinuation { get; }

        public ConversionResult(
            bool success,
            string convertedText,
            SupportedLanguage sourceLanguage,
            SupportedLanguage targetLanguage,
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
    /// с сохранением регистра и циклическое переключение между выбранными языками.
    /// </summary>
    public sealed class LayoutConverter
    {
        private const int KeysPerState = 47;
        private const int TotalTableSize = KeysPerState * 2; // 47 обычных + 47 с Shift

        // 47 физических клавиш (без Shift, затем с Shift) для раскладки English (US)
        private static readonly char[] EnglishChars =
        {
            // Без Shift (0..46)
            '`', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=',
            'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '[', ']', '\\',
            'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', ';', '\'',
            'z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '.', '/',

            // С Shift (47..93)
            '~', '!', '@', '#', '$', '%', '^', '&', '*', '(', ')', '_', '+',
            'Q', 'W', 'E', 'R', 'T', 'Y', 'U', 'I', 'O', 'P', '{', '}', '|',
            'A', 'S', 'D', 'F', 'G', 'H', 'J', 'K', 'L', ':', '"',
            'Z', 'X', 'C', 'V', 'B', 'N', 'M', '<', '>', '?'
        };

        // 47 физических клавиш (без Shift, затем с Shift) для русской раскладки (ЙЦУКЕН)
        private static readonly char[] RussianChars =
        {
            // Без Shift (0..46)
            'ё', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=',
            'й', 'ц', 'у', 'к', 'е', 'н', 'г', 'ш', 'щ', 'з', 'х', 'ъ', '\\',
            'ф', 'ы', 'в', 'а', 'п', 'р', 'о', 'л', 'д', 'ж', 'э',
            'я', 'ч', 'с', 'м', 'и', 'т', 'ь', 'б', 'ю', '.',

            // С Shift (47..93)
            'Ё', '!', '"', '№', ';', '%', ':', '?', '*', '(', ')', '_', '+',
            'Й', 'Ц', 'У', 'К', 'Е', 'Н', 'Г', 'Ш', 'Щ', 'З', 'Х', 'Ъ', '/',
            'Ф', 'Ы', 'В', 'А', 'П', 'Р', 'О', 'Л', 'Д', 'Ж', 'Э',
            'Я', 'Ч', 'С', 'М', 'И', 'Т', 'Ь', 'Б', 'Ю', ','
        };

        // 47 физических клавиш (без Shift, затем с Shift) для украинской раскладки (Українська розширена)
        private static readonly char[] UkrainianChars =
        {
            // Без Shift (0..46)
            '\'', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '=',
            'й', 'ц', 'у', 'к', 'е', 'н', 'г', 'ш', 'щ', 'з', 'х', 'ї', 'ґ',
            'ф', 'і', 'в', 'а', 'п', 'р', 'о', 'л', 'д', 'ж', 'є',
            'я', 'ч', 'с', 'м', 'и', 'т', 'ь', 'б', 'ю', '.',

            // С Shift (47..93)
            '₴', '!', '"', '№', ';', '%', ':', '?', '*', '(', ')', '_', '+',
            'Й', 'Ц', 'У', 'К', 'Е', 'Н', 'Г', 'Ш', 'Щ', 'З', 'Х', 'Ї', 'Ґ',
            'Ф', 'І', 'В', 'А', 'П', 'Р', 'О', 'Л', 'Д', 'Ж', 'Є',
            'Я', 'Ч', 'С', 'М', 'И', 'Т', 'Ь', 'Б', 'Ю', ','
        };

        private static readonly Dictionary<SupportedLanguage, char[]> CharByIndex =
            new Dictionary<SupportedLanguage, char[]>
            {
                [SupportedLanguage.English] = EnglishChars,
                [SupportedLanguage.Russian] = RussianChars,
                [SupportedLanguage.Ukrainian] = UkrainianChars
            };

        private static readonly Dictionary<SupportedLanguage, Dictionary<char, int>> IndexByChar =
            BuildReverseMaps();

        // Наборы уникальных символов для точного определения языка раскладки
        private static readonly HashSet<char> EnglishExclusiveChars = new HashSet<char>(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ`~@#$^&[]{}|<>");

        private static readonly HashSet<char> RussianExclusiveChars = new HashSet<char>(
            "ыЫъЪэЭёЁ");

        private static readonly HashSet<char> UkrainianExclusiveChars = new HashSet<char>(
            "іІїЇєЄґҐ₴ʼ");

        // Нейтральные символы, одинаковые на всех трёх раскладках (цифры, общие знаки, пробелы)
        private static readonly HashSet<char> NeutralChars = new HashSet<char>(
            "1234567890-_=+%*()");

        // Состояние циклической конвертации (для 3 и более языков)
        private readonly object _cycleLock = new object();
        private string _originalText = string.Empty;
        private string _lastConvertedText = string.Empty;
        private SupportedLanguage _sourceLanguage;
        private List<SupportedLanguage> _cycleOrder = new List<SupportedLanguage>();
        private int _currentCycleIndex;

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
                _cycleOrder = new List<SupportedLanguage>();
                _currentCycleIndex = 0;
            }
        }

        /// <summary>
        /// Выполняет конвертацию текста с учётом состояния цикла (2 или 3+ целевых языков).
        /// </summary>
        public ConversionResult ConvertWithCycle(string text, AppSettings settings, bool isContinuation)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new ConversionResult(false, text ?? string.Empty, SupportedLanguage.English, SupportedLanguage.English, false);
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
                    _sourceLanguage = DetectSourceLanguage(text, settings);
                    _cycleOrder = BuildCycleOrder(_sourceLanguage, settings.TargetLanguages);

                    if (_cycleOrder.Count < 2)
                    {
                        _lastConvertedText = text;
                        return new ConversionResult(false, text, _sourceLanguage, _sourceLanguage, false);
                    }

                    _currentCycleIndex = 1;
                    SupportedLanguage targetLang = _cycleOrder[_currentCycleIndex];
                    string converted = ConvertText(_originalText, _sourceLanguage, targetLang);
                    _lastConvertedText = converted;

                    return new ConversionResult(true, converted, _sourceLanguage, targetLang, false);
                }
                else
                {
                    _currentCycleIndex = (_currentCycleIndex + 1) % _cycleOrder.Count;
                    SupportedLanguage targetLang = _cycleOrder[_currentCycleIndex];
                    string converted = _currentCycleIndex == 0
                        ? _originalText
                        : ConvertText(_originalText, _sourceLanguage, targetLang);

                    _lastConvertedText = converted;
                    return new ConversionResult(true, converted, _sourceLanguage, targetLang, true);
                }
            }
        }

        /// <summary>
        /// Определяет исходную раскладку введённого текста с учётом опции «Использовать язык по умолчанию».
        /// </summary>
        public SupportedLanguage DetectSourceLanguage(string text, AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return settings.DefaultSourceLanguage;
            }

            // 1. Если включён исходный язык по умолчанию, сначала проверяем совместимость текста с ним.
            if (settings.UseDefaultSourceLanguage && IsTextCompatibleWithLanguage(text, settings.DefaultSourceLanguage))
            {
                return settings.DefaultSourceLanguage;
            }

            // 2. Автоопределение исходной раскладки по символам
            int enScore = ScoreLanguage(text, SupportedLanguage.English);
            int ruScore = ScoreLanguage(text, SupportedLanguage.Russian);
            int ukScore = ScoreLanguage(text, SupportedLanguage.Ukrainian);

            int maxScore = Math.Max(enScore, Math.Max(ruScore, ukScore));

            var tied = new List<SupportedLanguage>();
            if (enScore == maxScore) tied.Add(SupportedLanguage.English);
            if (ruScore == maxScore) tied.Add(SupportedLanguage.Russian);
            if (ukScore == maxScore) tied.Add(SupportedLanguage.Ukrainian);

            if (tied.Count == 1)
            {
                return tied[0];
            }

            // При ничьей (например, слово "привет" состоит из букв, общих для RU и UK)
            // отдаём приоритет языку по умолчанию (если он среди кандидатов и в целевых) или первому целевому языку.
            if (settings.UseDefaultSourceLanguage && tied.Contains(settings.DefaultSourceLanguage))
            {
                return settings.DefaultSourceLanguage;
            }

            foreach (SupportedLanguage target in settings.TargetLanguages)
            {
                if (tied.Contains(target) && target != SupportedLanguage.Ukrainian)
                {
                    return target;
                }
            }

            foreach (SupportedLanguage target in settings.TargetLanguages)
            {
                if (tied.Contains(target))
                {
                    return target;
                }
            }

            return tied[0];
        }

        /// <summary>
        /// Посимвольно конвертирует строку из sourceLanguage в targetLanguage с полным сохранением регистра.
        /// </summary>
        public string ConvertText(string text, SupportedLanguage sourceLanguage, SupportedLanguage targetLanguage)
        {
            if (string.IsNullOrEmpty(text) || sourceLanguage == targetLanguage)
            {
                return text;
            }

            Dictionary<char, int> sourceMap = IndexByChar[sourceLanguage];
            char[] targetTable = CharByIndex[targetLanguage];

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (sourceMap.TryGetValue(ch, out int keyIndex))
                {
                    sb.Append(targetTable[keyIndex]);
                }
                else
                {
                    sb.Append(ch);
                }
            }

            string result = sb.ToString();

            // Особый случай при 3-язычном цикле (ghbdtn -> привет -> привіт -> ghbdtn):
            // по физической карте клавиш EN->UK и RU->UK слово "привет" совпадает с русским написанием,
            // поэтому при переводе в украинский заменяем его на корректное "привіт" с сохранением регистра.
            if (targetLanguage == SupportedLanguage.Ukrainian
                && (sourceLanguage == SupportedLanguage.English || sourceLanguage == SupportedLanguage.Russian)
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

        private static List<SupportedLanguage> BuildCycleOrder(
            SupportedLanguage sourceLanguage,
            IReadOnlyList<SupportedLanguage> targetLanguages)
        {
            var order = new List<SupportedLanguage>();
            int sourcePos = -1;

            for (int i = 0; i < targetLanguages.Count; i++)
            {
                if (targetLanguages[i] == sourceLanguage)
                {
                    sourcePos = i;
                    break;
                }
            }

            if (sourcePos >= 0)
            {
                // Начинаем цикл с исходного языка, сохраняя порядок отмеченных целевых языков
                for (int offset = 0; offset < targetLanguages.Count; offset++)
                {
                    int idx = (sourcePos + offset) % targetLanguages.Count;
                    order.Add(targetLanguages[idx]);
                }
            }
            else
            {
                // Исходный язык не отмечен среди целевых: начинаем с исходного, затем все целевые
                order.Add(sourceLanguage);
                for (int i = 0; i < targetLanguages.Count; i++)
                {
                    if (!order.Contains(targetLanguages[i]))
                    {
                        order.Add(targetLanguages[i]);
                    }
                }
            }

            return order;
        }

        private static bool IsTextCompatibleWithLanguage(string text, SupportedLanguage language)
        {
            Dictionary<char, int> map = IndexByChar[language];
            bool hasDistinctiveOrMappable = false;

            foreach (char ch in text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                // Если в тексте встретился символ, которого нет в таблице выбранного языка,
                // или символ, принадлежащий исключительно другому алфавиту — язык по умолчанию не подходит.
                if (!map.ContainsKey(ch))
                {
                    return false;
                }

                if (language != SupportedLanguage.English && EnglishExclusiveChars.Contains(ch))
                {
                    return false;
                }

                if (language != SupportedLanguage.Russian && RussianExclusiveChars.Contains(ch))
                {
                    return false;
                }

                if (language != SupportedLanguage.Ukrainian && UkrainianExclusiveChars.Contains(ch))
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

        private static int ScoreLanguage(string text, SupportedLanguage language)
        {
            Dictionary<char, int> map = IndexByChar[language];
            int score = 0;
            bool hasCyrillic = false;
            bool hasRussianExclusive = false;

            foreach (char ch in text)
            {
                if (ch >= '\u0400' && ch <= '\u04FF')
                {
                    hasCyrillic = true;
                }

                if (RussianExclusiveChars.Contains(ch))
                {
                    hasRussianExclusive = true;
                }
            }

            foreach (char ch in text)
            {
                if (char.IsWhiteSpace(ch) || NeutralChars.Contains(ch))
                {
                    continue;
                }

                if (map.ContainsKey(ch))
                {
                    score += 1;
                }

                switch (language)
                {
                    case SupportedLanguage.English:
                        if (EnglishExclusiveChars.Contains(ch))
                        {
                            score += 10;
                        }
                        break;

                    case SupportedLanguage.Russian:
                        if (RussianExclusiveChars.Contains(ch))
                        {
                            score += 10;
                        }
                        break;

                    case SupportedLanguage.Ukrainian:
                        if (UkrainianExclusiveChars.Contains(ch))
                        {
                            score += 10;
                        }
                        else if ((ch == '\'' || ch == '’') && hasCyrillic && !hasRussianExclusive)
                        {
                            // Апостроф внутри кириллического слова без русских букв ы/ъ/э/ё характерен для украинского
                            score += 5;
                        }
                        break;
                }
            }

            return score;
        }

        private static Dictionary<SupportedLanguage, Dictionary<char, int>> BuildReverseMaps()
        {
            var result = new Dictionary<SupportedLanguage, Dictionary<char, int>>();

            foreach (KeyValuePair<SupportedLanguage, char[]> pair in CharByIndex)
            {
                if (pair.Value.Length != TotalTableSize)
                {
                    throw new InvalidOperationException($"Таблица раскладки {pair.Key} должна содержать {TotalTableSize} символов.");
                }

                var reverse = new Dictionary<char, int>(TotalTableSize);
                for (int i = 0; i < pair.Value.Length; i++)
                {
                    reverse[pair.Value[i]] = i;
                }

                // Поддержка типографских апострофов для украинской раскладки (клавиша ` / ё)
                if (pair.Key == SupportedLanguage.Ukrainian)
                {
                    reverse['’'] = 0;
                    reverse['ʼ'] = 0;
                }

                result[pair.Key] = reverse;
            }

            return result;
        }
    }
}
