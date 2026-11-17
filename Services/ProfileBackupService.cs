using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Итоги импорта профиля из резервной копии.
    /// </summary>
    public sealed class ProfileImportSummary
    {
        public ProfileImportMode Mode { get; }
        public AppSettings UpdatedSettings { get; }
        public int UserDictionaryCount { get; }
        public int IgnoredWordsCount { get; }
        public int AutoReplaceCount { get; }
        public int AppBlacklistCount { get; }

        public ProfileImportSummary(
            ProfileImportMode mode,
            AppSettings updatedSettings,
            int userDictionaryCount,
            int ignoredWordsCount,
            int autoReplaceCount,
            int appBlacklistCount)
        {
            Mode = mode;
            UpdatedSettings = updatedSettings;
            UserDictionaryCount = userDictionaryCount;
            IgnoredWordsCount = ignoredWordsCount;
            AutoReplaceCount = autoReplaceCount;
            AppBlacklistCount = appBlacklistCount;
        }
    }

    /// <summary>
    /// Выполняет полный экспорт и импорт профиля пользователя (все 5 конфигурационных файлов)
    /// в один структурированный JSON-файл резервной копии с поддержкой режимов Merge и Overwrite.
    /// </summary>
    public sealed class ProfileBackupService
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly SettingsManager _settingsManager;
        private readonly DictionaryService _dictionaryService;
        private readonly AutoReplaceService _autoReplaceService;
        private readonly AppExclusionService _appExclusionService;

        public ProfileBackupService(
            SettingsManager settingsManager,
            DictionaryService dictionaryService,
            AutoReplaceService autoReplaceService,
            AppExclusionService appExclusionService)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _dictionaryService = dictionaryService ?? throw new ArgumentNullException(nameof(dictionaryService));
            _autoReplaceService = autoReplaceService ?? throw new ArgumentNullException(nameof(autoReplaceService));
            _appExclusionService = appExclusionService ?? throw new ArgumentNullException(nameof(appExclusionService));
        }

        public static string GetDefaultBackupFileName() =>
            $"LanguageSwitcher_Backup_{DateTime.Now:yyyy-MM-dd}.json";

        /// <summary>
        /// Сохраняет все настройки, словари, исключения, правила автозамены и чёрный список программ в один JSON-файл.
        /// </summary>
        public void ExportProfile(string filePath, AppSettings currentSettings)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("Укажите путь к файлу резервной копии.", nameof(filePath));
            }

            if (currentSettings == null)
            {
                throw new ArgumentNullException(nameof(currentSettings));
            }

            AppSettings cleanSettings = currentSettings.Clone();
            cleanSettings.Normalize();

            var backup = new ProfileBackupData
            {
                FormatVersion = 1,
                ExportedAt = DateTime.UtcNow,
                Settings = cleanSettings,
                IgnoredWords = _dictionaryService.GetIgnoredWordsSnapshot(),
                UserDictionary = _dictionaryService.GetUserDictionarySnapshot(),
                AutoReplaceRules = _autoReplaceService.GetRulesSnapshot(),
                AppBlacklist = _appExclusionService.GetBlacklistSnapshot()
            };

            string json = JsonSerializer.Serialize(backup, SettingsManager.JsonOptions);
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(filePath, json, Utf8NoBom);
        }

        /// <summary>
        /// Загружает и валидирует файл резервной копии.
        /// </summary>
        public ProfileBackupData? ReadAndValidateBackup(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // Проверяем наличие ключевых разделов резервной копии
                if (!root.TryGetProperty("FormatVersion", out JsonElement versionEl)
                    || versionEl.ValueKind != JsonValueKind.Number
                    || !versionEl.TryGetInt32(out int version)
                    || version < 1
                    || !root.TryGetProperty("Settings", out JsonElement settingsEl)
                    || settingsEl.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                ProfileBackupData? parsed = JsonSerializer.Deserialize<ProfileBackupData>(json, SettingsManager.JsonOptions);
                if (parsed == null || parsed.Settings == null)
                {
                    return null;
                }

                parsed.Settings.Normalize();
                parsed.IgnoredWords ??= new List<string>();
                parsed.UserDictionary ??= new List<UserDictionaryEntry>();
                parsed.AutoReplaceRules ??= new List<AutoReplaceRule>();
                parsed.AppBlacklist ??= new List<string>();
                return parsed;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Восстанавливает профиль в выбранном режиме (Merge или Overwrite),
        /// сразу сохраняет все 5 JSON-файлов на диск и синхронизирует автозапуск Windows.
        /// </summary>
        public ProfileImportSummary ApplyBackup(
            ProfileBackupData backup,
            ProfileImportMode mode,
            AppSettings targetSettings,
            LocalizationService? localizationService = null,
            bool syncRegistry = true)
        {
            if (backup == null)
            {
                throw new ArgumentNullException(nameof(backup));
            }

            if (targetSettings == null)
            {
                throw new ArgumentNullException(nameof(targetSettings));
            }

            CopySettingsValues(backup.Settings, targetSettings);
            targetSettings.Normalize();

            if (mode == ProfileImportMode.Overwrite)
            {
                _dictionaryService.ReplaceIgnoredWords(backup.IgnoredWords);
                _dictionaryService.ReplaceUserDictionary(backup.UserDictionary);
                _autoReplaceService.ReplaceAllRules(backup.AutoReplaceRules);
                _appExclusionService.ReplaceBlacklist(backup.AppBlacklist);
            }
            else
            {
                // Merge: добавляем отсутствующие записи к текущим без удаления существующих
                _dictionaryService.AddIgnoredWords(backup.IgnoredWords);

                foreach (IGrouping<string, UserDictionaryEntry> group in backup.UserDictionary
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Word))
                    .GroupBy(e => UniversalLayoutService.Instance.NormalizeKlid(e.Language), StringComparer.OrdinalIgnoreCase))
                {
                    _dictionaryService.AddUserWords(group.Select(g => g.Word), group.Key);
                }

                List<AutoReplaceRule> mergedRules = _autoReplaceService.GetRulesSnapshot();
                var existingTriggers = new HashSet<string>(
                    mergedRules.Select(r => r.Trigger),
                    StringComparer.OrdinalIgnoreCase);

                foreach (AutoReplaceRule rule in backup.AutoReplaceRules)
                {
                    if (rule == null || string.IsNullOrWhiteSpace(rule.Trigger))
                    {
                        continue;
                    }

                    string trimmedTrigger = rule.Trigger.Trim();
                    if (existingTriggers.Add(trimmedTrigger))
                    {
                        mergedRules.Add(new AutoReplaceRule
                        {
                            Trigger = trimmedTrigger,
                            Replacement = rule.Replacement ?? string.Empty,
                            IsEnabled = rule.IsEnabled
                        });
                    }
                }

                _autoReplaceService.ReplaceAllRules(mergedRules);

                foreach (string exe in backup.AppBlacklist)
                {
                    _appExclusionService.AddExecutable(exe);
                }
            }

            _settingsManager.Save(targetSettings);

            if (syncRegistry)
            {
                _settingsManager.SetStartupEnabled(targetSettings.RunAtStartup);
            }

            localizationService?.ApplyLanguage(targetSettings.InterfaceLanguage);

            return new ProfileImportSummary(
                mode,
                targetSettings,
                _dictionaryService.GetUserWordCount(),
                _dictionaryService.GetIgnoredWordsSnapshot().Count,
                _autoReplaceService.Count,
                _appExclusionService.Count);
        }

        private static void CopySettingsValues(AppSettings source, AppSettings destination)
        {
            destination.InterfaceLanguage = source.InterfaceLanguage;
            destination.UseDefaultSourceLanguage = source.UseDefaultSourceLanguage;
            destination.DefaultSourceLanguage = source.DefaultSourceLanguage;
            destination.AddedLanguages = new List<string>(source.AddedLanguages);
            destination.TargetLanguages = new List<string>(source.TargetLanguages);
            destination.AutoSwitchLayout = source.AutoSwitchLayout;
            destination.AutoCorrectTypos = source.AutoCorrectTypos;
            destination.IgnoreProfanityInTypoCorrection = source.IgnoreProfanityInTypoCorrection;
            destination.FixDoubleCapitals = source.FixDoubleCapitals;
            destination.EnableCustomAutoReplace = source.EnableCustomAutoReplace;
            destination.EnableAppExclusions = source.EnableAppExclusions;
            destination.AutoPauseInFullscreen = source.AutoPauseInFullscreen;
            destination.BlockManualTriggerInExcluded = source.BlockManualTriggerInExcluded;
            destination.TriggerVirtualKey = source.TriggerVirtualKey;
            destination.TriggerModifiers = source.TriggerModifiers;
            destination.TriggerRequireDoubleTap = source.TriggerRequireDoubleTap;
            destination.RunAtStartup = source.RunAtStartup;
            destination.IsPaused = source.IsPaused;
        }
    }
}
