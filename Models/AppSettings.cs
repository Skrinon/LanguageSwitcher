using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Input;
using LanguageSwitcher.Services;

namespace LanguageSwitcher.Models
{
    /// <summary>
    /// Утилита форматирования и проверки виртуальных кодов клавиш Windows для пользовательского триггера.
    /// </summary>
    public static class HotkeyFormatter
    {
        public const uint DefaultTriggerVirtualKey = 0xA4; // VK_LMENU (Left Alt)

        public const uint ModCtrl = 1;
        public const uint ModAlt = 2;
        public const uint ModShift = 4;
        public const uint ModWin = 8;

        public static bool IsModifierVirtualKey(uint vkCode)
        {
            return vkCode == 0x10 || vkCode == 0x11 || vkCode == 0x12
                || vkCode == 0x5B || vkCode == 0x5C
                || (vkCode >= 0xA0 && vkCode <= 0xA5);
        }

        public static uint GetModifierBit(uint vkCode)
        {
            return vkCode switch
            {
                0x11 or 0xA2 or 0xA3 => ModCtrl,
                0x12 or 0xA4 or 0xA5 => ModAlt,
                0x10 or 0xA0 or 0xA1 => ModShift,
                0x5B or 0x5C => ModWin,
                _ => 0
            };
        }

        public static string GetKeyDisplayName(uint vkCode)
        {
            switch (vkCode)
            {
                case 0xA4: return "Left Alt";
                case 0xA5: return "Right Alt";
                case 0x12: return "Alt";
                case 0xA2: return "Left Ctrl";
                case 0xA3: return "Right Ctrl";
                case 0x11: return "Ctrl";
                case 0xA0: return "Left Shift";
                case 0xA1: return "Right Shift";
                case 0x10: return "Shift";
                case 0x5B: return "Left Win";
                case 0x5C: return "Right Win";
                case 0x13: return "Pause / Break";
                case 0x14: return "Caps Lock";
                case 0x91: return "Scroll Lock";
                case 0x90: return "Num Lock";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "Page Up";
                case 0x22: return "Page Down";
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x2C: return "Print Screen";
                case 0xC0: return "~ (`)";
                case 0xBA: return "; (:)";
                case 0xBB: return "= (+)";
                case 0xBC: return ", (<)";
                case 0xBD: return "- (_)";
                case 0xBE: return ". (>)";
                case 0xBF: return "/ (?)";
                case 0xDB: return "[ ({)";
                case 0xDC: return "\\ (|)";
                case 0xDD: return "] (})";
                case 0xDE: return "' (\")";
                case 0xE2: return "ISO Backslash";
                case 0x6A: return "Num *";
                case 0x6B: return "Num +";
                case 0x6D: return "Num -";
                case 0x6E: return "Num .";
                case 0x6F: return "Num /";
            }

            if (vkCode >= 0x70 && vkCode <= 0x87)
            {
                return $"F{vkCode - 0x6F}";
            }

            if (vkCode >= 0x30 && vkCode <= 0x39)
            {
                return ((char)vkCode).ToString();
            }

            if (vkCode >= 0x41 && vkCode <= 0x5A)
            {
                return ((char)vkCode).ToString();
            }

            if (vkCode >= 0x60 && vkCode <= 0x69)
            {
                return $"Num {vkCode - 0x60}";
            }

            Key wpfKey = KeyInterop.KeyFromVirtualKey((int)vkCode);
            return wpfKey != Key.None ? wpfKey.ToString() : $"VK 0x{vkCode:X2}";
        }

        public static string FormatTrigger(uint vkCode, uint modifiers, bool requireDoubleTap)
        {
            uint modMask = modifiers & ~GetModifierBit(vkCode);
            var parts = new List<string>(5);

            if ((modMask & ModCtrl) != 0) parts.Add("Ctrl");
            if ((modMask & ModAlt) != 0) parts.Add("Alt");
            if ((modMask & ModShift) != 0) parts.Add("Shift");
            if ((modMask & ModWin) != 0) parts.Add("Win");
            parts.Add(GetKeyDisplayName(vkCode));

            string baseText = string.Join(" + ", parts);
            return requireDoubleTap ? $"{baseText} × 2" : baseText;
        }
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
    /// Динамические метаданные клавиатурной раскладки Windows (KLID / LCID / культура / бейдж).
    /// </summary>
    public sealed class KeyboardLayoutInfo
    {
        public string Klid { get; init; } = UniversalLayoutService.EnglishUsKlid;
        public string Id => Klid;
        public string CultureCode { get; init; } = "en-US";
        public string IsoLanguageCode { get; init; } = "en";
        public string DisplayName { get; init; } = "English (US)";
        public string NativeName { get; init; } = "English (United States)";
        public string ShortBadge { get; init; } = "EN";
        public ushort LanguageId { get; init; } = 0x0409;
        public bool IsInstalledInWindows { get; set; }

        [JsonIgnore]
        public string DisplayLabel => $"[{ShortBadge}] {DisplayName}";

        [JsonIgnore]
        public string PickerDisplayLabel => IsInstalledInWindows
            ? $"[{ShortBadge}] {DisplayName} ({CultureCode} • KLID {Klid}) ★"
            : $"[{ShortBadge}] {DisplayName} ({CultureCode} • KLID {Klid})";
    }

    /// <summary>
    /// Запись пользовательского словаря с привязкой к KLID языка/раскладки.
    /// </summary>
    public sealed class UserDictionaryEntry
    {
        public string Word { get; set; } = string.Empty;

        /// <summary>
        /// Идентификатор раскладки Windows (KLID в формате "00000409" или код языка для обратной совместимости).
        /// </summary>
        public string Language { get; set; } = UniversalLayoutService.RussianKlid;

        [JsonIgnore]
        public string LanguageCode => UniversalLayoutService.Instance.GetLayoutInfo(Language).ShortBadge;
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
        public string OriginalLanguage { get; init; } = UniversalLayoutService.EnglishUsKlid;
        public string TargetLanguage { get; init; } = UniversalLayoutService.EnglishUsKlid;
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
        /// KLID исходного языка по умолчанию, используемого в первую очередь при включённой опции.
        /// </summary>
        public string DefaultSourceLanguage { get; set; } = UniversalLayoutService.EnglishUsKlid;

        /// <summary>
        /// Все языки/раскладки (KLID), добавленные пользователем в рабочий список.
        /// </summary>
        public List<string> AddedLanguages { get; set; } = new List<string>();

        /// <summary>
        /// Отмеченные целевые языки (KLID) для циклической конвертации и автопереключения (минимум 2).
        /// </summary>
        [JsonPropertyName("TargetLanguages")]
        public List<string> TargetLanguages { get; set; } = new List<string>
        {
            UniversalLayoutService.EnglishUsKlid,
            UniversalLayoutService.RussianKlid
        };

        /// <summary>
        /// Автоматически менять раскладку при ошибочном вводе (Приоритет 2).
        /// </summary>
        public bool AutoSwitchLayout { get; set; } = true;

        /// <summary>
        /// Исправлять механические опечатки в словах по динамической матрице соседних клавиш (Приоритет 3).
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
        /// Виртуальный код (Win32 VK) клавиши-триггера для ручного переключения раскладки (по умолчанию 0xA4 = Left Alt).
        /// </summary>
        public uint TriggerVirtualKey { get; set; } = HotkeyFormatter.DefaultTriggerVirtualKey;

        /// <summary>
        /// Битовая маска модификаторов для триггера (1 = Ctrl, 2 = Alt, 4 = Shift, 8 = Win).
        /// </summary>
        public uint TriggerModifiers { get; set; } = 0;

        /// <summary>
        /// Требовать ли двойное быстрое нажатие клавиши-триггера (true — ×2, false — одиночное нажатие).
        /// </summary>
        public bool TriggerRequireDoubleTap { get; set; } = true;

        /// <summary>
        /// Блокировать ли также ручной триггер переключения в исключённых/полноэкранных окнах.
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
        /// Приводит настройки к валидному состоянию, мигрирует устаревшие строковые коды (English/Russian/Ukrainian)
        /// в канонические KLID и гарантирует минимум 2 целевых языка.
        /// </summary>
        public void Normalize()
        {
            UniversalLayoutService layoutService = UniversalLayoutService.Instance;

            if (string.IsNullOrWhiteSpace(InterfaceLanguage))
            {
                InterfaceLanguage = "ru";
            }

            if (TriggerVirtualKey == 0 || TriggerVirtualKey > 0xFE)
            {
                TriggerVirtualKey = HotkeyFormatter.DefaultTriggerVirtualKey;
            }

            TriggerModifiers &= (HotkeyFormatter.ModCtrl | HotkeyFormatter.ModAlt | HotkeyFormatter.ModShift | HotkeyFormatter.ModWin);
            TriggerModifiers &= ~HotkeyFormatter.GetModifierBit(TriggerVirtualKey);

            DefaultSourceLanguage = layoutService.NormalizeKlid(DefaultSourceLanguage);

            var cleanTargets = new List<string>();
            if (TargetLanguages != null)
            {
                foreach (string raw in TargetLanguages)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    string klid = layoutService.NormalizeKlid(raw);
                    if (!cleanTargets.Contains(klid, StringComparer.OrdinalIgnoreCase))
                    {
                        cleanTargets.Add(klid);
                    }
                }
            }

            var cleanAdded = new List<string>();
            bool wasAddedEmpty = AddedLanguages == null || AddedLanguages.Count == 0;
            if (AddedLanguages != null)
            {
                foreach (string raw in AddedLanguages)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    string klid = layoutService.NormalizeKlid(raw);
                    if (!cleanAdded.Contains(klid, StringComparer.OrdinalIgnoreCase))
                    {
                        cleanAdded.Add(klid);
                    }
                }
            }

            if (wasAddedEmpty)
            {
                // Автоматически подхватываем раскладки, установленные у пользователя в Windows
                foreach (string installedKlid in layoutService.GetInstalledWindowsKlids())
                {
                    if (!cleanAdded.Contains(installedKlid, StringComparer.OrdinalIgnoreCase))
                    {
                        cleanAdded.Add(installedKlid);
                    }
                }
            }

            foreach (string target in cleanTargets)
            {
                if (!cleanAdded.Contains(target, StringComparer.OrdinalIgnoreCase))
                {
                    cleanAdded.Add(target);
                }
            }

            // Если это первый запуск или в списке меньше 2 языков, гарантируем наличие базовых раскладок
            string[] defaultFallbacks =
            {
                UniversalLayoutService.EnglishUsKlid,
                UniversalLayoutService.RussianKlid,
                UniversalLayoutService.UkrainianKlid
            };

            if (wasAddedEmpty || cleanAdded.Count < 2)
            {
                foreach (string fb in defaultFallbacks)
                {
                    if (!cleanAdded.Contains(fb, StringComparer.OrdinalIgnoreCase))
                    {
                        cleanAdded.Add(fb);
                    }

                    if (!wasAddedEmpty && cleanAdded.Count >= 2)
                    {
                        break;
                    }
                }
            }

            if (cleanTargets.Count < 2)
            {
                foreach (string candidate in cleanAdded)
                {
                    if (!cleanTargets.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        cleanTargets.Add(candidate);
                    }

                    if (cleanTargets.Count >= 2)
                    {
                        break;
                    }
                }

                foreach (string fb in defaultFallbacks)
                {
                    if (cleanTargets.Count >= 2)
                    {
                        break;
                    }

                    if (!cleanTargets.Contains(fb, StringComparer.OrdinalIgnoreCase))
                    {
                        cleanTargets.Add(fb);
                        if (!cleanAdded.Contains(fb, StringComparer.OrdinalIgnoreCase))
                        {
                            cleanAdded.Add(fb);
                        }
                    }
                }
            }

            if (!cleanAdded.Contains(DefaultSourceLanguage, StringComparer.OrdinalIgnoreCase))
            {
                DefaultSourceLanguage = cleanAdded[0];
            }

            AddedLanguages = cleanAdded;
            TargetLanguages = cleanTargets;
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
                AddedLanguages = new List<string>(AddedLanguages),
                TargetLanguages = new List<string>(TargetLanguages),
                AutoSwitchLayout = AutoSwitchLayout,
                AutoCorrectTypos = AutoCorrectTypos,
                IgnoreProfanityInTypoCorrection = IgnoreProfanityInTypoCorrection,
                FixDoubleCapitals = FixDoubleCapitals,
                EnableCustomAutoReplace = EnableCustomAutoReplace,
                EnableAppExclusions = EnableAppExclusions,
                AutoPauseInFullscreen = AutoPauseInFullscreen,
                BlockManualTriggerInExcluded = BlockManualTriggerInExcluded,
                TriggerVirtualKey = TriggerVirtualKey,
                TriggerModifiers = TriggerModifiers,
                TriggerRequireDoubleTap = TriggerRequireDoubleTap,
                RunAtStartup = RunAtStartup,
                IsPaused = IsPaused
            };
        }
    }
}
