using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LanguageSwitcher.Models
{
    /// <summary>
    /// Поддерживаемые раскладки клавиатуры.
    /// </summary>
    public enum SupportedLanguage
    {
        English = 0,
        Russian = 1,
        Ukrainian = 2
    }

    /// <summary>
    /// Тип автоматического действия, выполненного при вводе разделителя слова.
    /// </summary>
    public enum AutoActionType
    {
        None = 0,
        CustomAutoReplace = 1,
        AutoLayoutSwitch = 2,
        TypoCorrection = 3,
        DoubleCapitalCorrection = 4
    }

    /// <summary>
    /// Режим восстановления профиля из файла резервной копии.
    /// </summary>
    public enum ProfileImportMode
    {
        Merge = 0,
        Overwrite = 1
    }

    /// <summary>
    /// Метаданные поддерживаемого языка и идентификаторы раскладки Windows (KLID / LCID).
    /// </summary>
    public sealed class LanguageInfo
    {
        public SupportedLanguage Language { get; }
        public string DisplayName { get; }
        public string ShortCode { get; }
        public string KeyboardLayoutId { get; }
        public ushort LanguageId { get; }

        public LanguageInfo(
            SupportedLanguage language,
            string displayName,
            string shortCode,
            string keyboardLayoutId,
            ushort languageId)
        {
            Language = language;
            DisplayName = displayName;
            ShortCode = shortCode;
            KeyboardLayoutId = keyboardLayoutId;
            LanguageId = languageId;
        }
    }

    /// <summary>
    /// Реестр метаданных для всех поддерживаемых языков.
    /// </summary>
    public static class LanguageCatalog
    {
        public static readonly IReadOnlyList<LanguageInfo> All = new[]
        {
            new LanguageInfo(SupportedLanguage.English, "English (US)", "EN", "00000409", 0x0409),
            new LanguageInfo(SupportedLanguage.Russian, "Русский", "RU", "00000419", 0x0419),
            new LanguageInfo(SupportedLanguage.Ukrainian, "Українська", "UA", "00000422", 0x0422)
        };

        public static LanguageInfo GetInfo(SupportedLanguage language)
        {
            foreach (LanguageInfo item in All)
            {
                if (item.Language == language)
                {
                    return item;
                }
            }

            return All[0];
        }

        public static SupportedLanguage? TryFromLanguageId(ushort langId)
        {
            foreach (LanguageInfo item in All)
            {
                if (item.LanguageId == langId)
                {
                    return item.Language;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Запись пользовательского словаря с привязкой к языку.
    /// </summary>
    public sealed class UserDictionaryEntry
    {
        public string Word { get; set; } = string.Empty;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SupportedLanguage Language { get; set; } = SupportedLanguage.Russian;

        [JsonIgnore]
        public string LanguageCode => LanguageCatalog.GetInfo(Language).ShortCode;
    }

    /// <summary>
    /// Правило пользовательской автозамены / сокращения.
    /// </summary>
    public sealed class AutoReplaceRule
    {
        public string Trigger { get; set; } = string.Empty;
        public string Replacement { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;

        [JsonIgnore]
        public string ReplacementPreview =>
            (Replacement ?? string.Empty).Replace("\r\n", " ↵ ").Replace("\n", " ↵ ");
    }

    /// <summary>
    /// Информация о последнем автоматическом преобразовании слова для умного отката по двойному Left Alt.
    /// </summary>
    public sealed class AutoActionRecord
    {
        public AutoActionType ActionType { get; init; }
        public string OriginalWord { get; init; } = string.Empty;
        public string ReplacedText { get; init; } = string.Empty;
        public string DelimiterText { get; init; } = string.Empty;
        public SupportedLanguage OriginalLanguage { get; init; }
        public SupportedLanguage TargetLanguage { get; init; }
        public IntPtr TargetWindowHwnd { get; init; }
        public double TimeSavedSeconds { get; init; }
    }

    /// <summary>
    /// Накопленная статистика исправлений и сэкономленного времени (statistics.json).
    /// </summary>
    public sealed class UsageStatisticsData
    {
        public long LayoutSwitchesCount { get; set; }
        public long TyposFixedCount { get; set; }
        public long DoubleCapitalsFixedCount { get; set; }
        public long AutoReplacementsCount { get; set; }
        public double EstimatedTimeSavedSeconds { get; set; }

        [JsonIgnore]
        public long TotalFixesCount =>
            LayoutSwitchesCount + TyposFixedCount + DoubleCapitalsFixedCount + AutoReplacementsCount;

        public UsageStatisticsData Clone()
        {
            return new UsageStatisticsData
            {
                LayoutSwitchesCount = LayoutSwitchesCount,
                TyposFixedCount = TyposFixedCount,
                DoubleCapitalsFixedCount = DoubleCapitalsFixedCount,
                AutoReplacementsCount = AutoReplacementsCount,
                EstimatedTimeSavedSeconds = EstimatedTimeSavedSeconds
            };
        }
    }

    /// <summary>
    /// Полная резервная копия профиля пользователя (settings + все 4 списка).
    /// </summary>
    public sealed class ProfileBackupData
    {
        public int FormatVersion { get; set; } = 1;
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
        public AppSettings Settings { get; set; } = new AppSettings();
        public List<string> IgnoredWords { get; set; } = new List<string>();
        public List<UserDictionaryEntry> UserDictionary { get; set; } = new List<UserDictionaryEntry>();
        public List<AutoReplaceRule> AutoReplaceRules { get; set; } = new List<AutoReplaceRule>();
        public List<string> AppBlacklist { get; set; } = new List<string>();
    }

    /// <summary>
    /// Пользовательские настройки приложения, сохраняемые в settings.json.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>
        /// Код выбранного языка интерфейса (имя файла в папке locales/).
        /// </summary>
        public string InterfaceLanguage { get; set; } = "ru";

        /// <summary>
        /// Использовать ли приоритетный исходный язык с неправильной раскладки.
        /// </summary>
        public bool UseDefaultSourceLanguage { get; set; } = false;

        /// <summary>
        /// Исходный язык по умолчанию, используемый в первую очередь при включённой опции.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SupportedLanguage DefaultSourceLanguage { get; set; } = SupportedLanguage.English;

        /// <summary>
        /// Отмеченные целевые языки для циклической конвертации (минимум 2).
        /// </summary>
        [JsonPropertyName("TargetLanguages")]
        public List<SupportedLanguage> TargetLanguages { get; set; } = new List<SupportedLanguage>
        {
            SupportedLanguage.English,
            SupportedLanguage.Russian
        };

        /// <summary>
        /// Автоматически менять раскладку при ошибочном вводе (Приоритет 2).
        /// </summary>
        public bool AutoSwitchLayout { get; set; } = true;

        /// <summary>
        /// Исправлять механические опечатки в словах по матрице соседних клавиш (Приоритет 3).
        /// </summary>
        public bool AutoCorrectTypos { get; set; } = true;

        /// <summary>
        /// Не исправлять маты и сленг на обычные слова при проверке опечаток.
        /// </summary>
        public bool IgnoreProfanityInTypoCorrection { get; set; } = true;

        /// <summary>
        /// Исправлять случайную вторую заглавную букву в начале слова (ПРивет -> Привет).
        /// </summary>
        public bool FixDoubleCapitals { get; set; } = true;

        /// <summary>
        /// Включить пользовательские автозамены и сокращения (Приоритет 1).
        /// </summary>
        public bool EnableCustomAutoReplace { get; set; } = true;

        /// <summary>
        /// Главный флаг включения чёрного списка программ и автопаузы в полноэкранном режиме.
        /// Когда false — проверка программ и полноэкранных игр полностью отключена.
        /// </summary>
        public bool EnableAppExclusions { get; set; } = false;

        /// <summary>
        /// Автоматически ставить на паузу в полноэкранных играх и приложениях (работает при EnableAppExclusions = true).
        /// </summary>
        public bool AutoPauseInFullscreen { get; set; } = true;

        /// <summary>
        /// Блокировать ли также ручной двойной Left Alt в исключённых/полноэкранных окнах.
        /// </summary>
        public bool BlockManualTriggerInExcluded { get; set; } = true;

        /// <summary>
        /// Запускать приложение автоматически вместе с Windows.
        /// </summary>
        public bool RunAtStartup { get; set; } = false;

        /// <summary>
        /// Приостановлен ли перехват триггера вручную.
        /// </summary>
        public bool IsPaused { get; set; } = false;

        /// <summary>
        /// Приводит настройки к валидному состоянию (удаляет дубликаты и гарантирует минимум 2 целевых языка).
        /// </summary>
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(InterfaceLanguage))
            {
                InterfaceLanguage = "ru";
            }

            var deduplicated = new List<SupportedLanguage>();
            if (TargetLanguages != null)
            {
                foreach (SupportedLanguage lang in TargetLanguages)
                {
                    if (Enum.IsDefined(typeof(SupportedLanguage), lang) && !deduplicated.Contains(lang))
                    {
                        deduplicated.Add(lang);
                    }
                }
            }

            if (deduplicated.Count < 2)
            {
                SupportedLanguage[] fallbacks =
                {
                    SupportedLanguage.English,
                    SupportedLanguage.Russian,
                    SupportedLanguage.Ukrainian
                };

                foreach (SupportedLanguage fallback in fallbacks)
                {
                    if (!deduplicated.Contains(fallback))
                    {
                        deduplicated.Add(fallback);
                    }

                    if (deduplicated.Count >= 2)
                    {
                        break;
                    }
                }
            }

            if (!Enum.IsDefined(typeof(SupportedLanguage), DefaultSourceLanguage))
            {
                DefaultSourceLanguage = SupportedLanguage.English;
            }

            TargetLanguages = deduplicated;
        }

        /// <summary>
        /// Создаёт глубокую копию объекта настроек.
        /// </summary>
        public AppSettings Clone()
        {
            return new AppSettings
            {
                InterfaceLanguage = InterfaceLanguage,
                UseDefaultSourceLanguage = UseDefaultSourceLanguage,
                DefaultSourceLanguage = DefaultSourceLanguage,
                TargetLanguages = new List<SupportedLanguage>(TargetLanguages),
                AutoSwitchLayout = AutoSwitchLayout,
                AutoCorrectTypos = AutoCorrectTypos,
                IgnoreProfanityInTypoCorrection = IgnoreProfanityInTypoCorrection,
                FixDoubleCapitals = FixDoubleCapitals,
                EnableCustomAutoReplace = EnableCustomAutoReplace,
                EnableAppExclusions = EnableAppExclusions,
                AutoPauseInFullscreen = AutoPauseInFullscreen,
                BlockManualTriggerInExcluded = BlockManualTriggerInExcluded,
                RunAtStartup = RunAtStartup,
                IsPaused = IsPaused
            };
        }
    }
}
