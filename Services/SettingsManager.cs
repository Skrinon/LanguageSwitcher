using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanguageSwitcher.Models;
using Microsoft.Win32;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Управляет единой директорией пользовательских данных и мгновенным сохранением всех конфигурационных JSON-файлов
    /// (settings.json, ignored_words.json, user_dictionary.json, autoreplace.json, app_blacklist.json),
    /// а также параметром автозапуска Windows в реестре.
    /// </summary>
    public sealed class SettingsManager
    {
        private const string SettingsFileName = "settings.json";
        private const string IgnoredWordsFileName = "ignored_words.json";
        private const string UserDictionaryFileName = "user_dictionary.json";
        private const string AutoReplaceFileName = "autoreplace.json";
        private const string AppBlacklistFileName = "app_blacklist.json";
        private const string StatisticsFileName = "statistics.json";

        private const string AppFolderName = "LanguageSwitcher";
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "LanguageSwitcher";

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Единая папка хранения всех 5 JSON-файлов конфигурации.
        /// </summary>
        public string DataDirectory { get; private set; }

        public string SettingsFilePath => Path.Combine(DataDirectory, SettingsFileName);
        public string IgnoredWordsFilePath => Path.Combine(DataDirectory, IgnoredWordsFileName);
        public string UserDictionaryFilePath => Path.Combine(DataDirectory, UserDictionaryFileName);
        public string AutoReplaceFilePath => Path.Combine(DataDirectory, AutoReplaceFileName);
        public string AppBlacklistFilePath => Path.Combine(DataDirectory, AppBlacklistFileName);
        public string StatisticsFilePath => Path.Combine(DataDirectory, StatisticsFileName);

        public SettingsManager(string? customDataDirectory = null)
        {
            DataDirectory = string.IsNullOrWhiteSpace(customDataDirectory)
                ? ResolveDataDirectory()
                : customDataDirectory;
        }

        /// <summary>
        /// Загружает основные настройки из settings.json или создаёт файл по умолчанию.
        /// </summary>
        public AppSettings Load(bool syncStartupFromRegistry = true)
        {
            AppSettings settings = LoadJsonFile(SettingsFileName, () => new AppSettings(), createIfMissing: true);
            settings.Normalize();

            if (syncStartupFromRegistry)
            {
                settings.RunAtStartup = IsStartupEnabledInRegistry();
            }

            return settings;
        }

        /// <summary>
        /// Сохраняет основные настройки в settings.json.
        /// </summary>
        public void Save(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.Normalize();
            SaveJsonFile(SettingsFileName, settings);
        }

        public List<string> LoadIgnoredWords() =>
            LoadJsonFile(IgnoredWordsFileName, () => new List<string>(), createIfMissing: true);

        public void SaveIgnoredWords(IEnumerable<string> words) =>
            SaveJsonFile(IgnoredWordsFileName, new List<string>(words));

        public List<UserDictionaryEntry> LoadUserDictionary() =>
            LoadJsonFile(UserDictionaryFileName, () => new List<UserDictionaryEntry>(), createIfMissing: true);

        public void SaveUserDictionary(IEnumerable<UserDictionaryEntry> entries) =>
            SaveJsonFile(UserDictionaryFileName, new List<UserDictionaryEntry>(entries));

        public List<AutoReplaceRule> LoadAutoReplaceRules() =>
            LoadJsonFile(AutoReplaceFileName, () => new List<AutoReplaceRule>(), createIfMissing: true);

        public void SaveAutoReplaceRules(IEnumerable<AutoReplaceRule> rules) =>
            SaveJsonFile(AutoReplaceFileName, new List<AutoReplaceRule>(rules));

        public List<string> LoadAppBlacklist() =>
            LoadJsonFile(AppBlacklistFileName, () => new List<string>(), createIfMissing: true);

        public void SaveAppBlacklist(IEnumerable<string> executables) =>
            SaveJsonFile(AppBlacklistFileName, new List<string>(executables));

        public UsageStatisticsData LoadStatistics() =>
            LoadJsonFile(StatisticsFileName, () => new UsageStatisticsData(), createIfMissing: true);

        public void SaveStatistics(UsageStatisticsData stats) =>
            SaveJsonFile(StatisticsFileName, stats);

        /// <summary>
        /// Включает или отключает автозапуск приложения через HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
        /// </summary>
        public bool SetStartupEnabled(bool enable)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true)
                    ?? Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true);

                if (key == null)
                {
                    return false;
                }

                if (enable)
                {
                    string? exePath = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        return false;
                    }

                    key.SetValue(StartupValueName, $"\"{exePath}\" --minimized");
                }
                else
                {
                    key.DeleteValue(StartupValueName, throwOnMissingValue: false);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Проверяет, прописано ли приложение в ветке автозапуска текущего пользователя.
        /// </summary>
        public bool IsStartupEnabledInRegistry()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
                object? value = key?.GetValue(StartupValueName);
                return value is string str && !string.IsNullOrWhiteSpace(str);
            }
            catch
            {
                return false;
            }
        }

        private T LoadJsonFile<T>(string fileName, Func<T> createDefault, bool createIfMissing)
        {
            string filePath = Path.Combine(DataDirectory, fileName);

            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath, Encoding.UTF8);
                    T? deserialized = JsonSerializer.Deserialize<T>(json, JsonOptions);
                    if (deserialized != null)
                    {
                        return deserialized;
                    }
                }
                else if (createIfMissing)
                {
                    T initial = createDefault();
                    SaveJsonFile(fileName, initial);
                    return initial;
                }
            }
            catch
            {
                // При повреждении JSON возвращаем безопасную пустую структуру
            }

            return createDefault();
        }

        private void SaveJsonFile<T>(string fileName, T data)
        {
            string json = JsonSerializer.Serialize(data, JsonOptions);
            string targetPath = Path.Combine(DataDirectory, fileName);

            try
            {
                EnsureDirectoryExists(DataDirectory);
                File.WriteAllText(targetPath, json, Utf8NoBom);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                // При невозможности записи в папку программы переносим единую директорию данных в %AppData%
                string appDataDir = GetAppDataDirectory();
                if (!string.Equals(DataDirectory, appDataDir, StringComparison.OrdinalIgnoreCase))
                {
                    MigrateDirectoryIfNeeded(DataDirectory, appDataDir);
                    DataDirectory = appDataDir;
                    EnsureDirectoryExists(DataDirectory);
                    File.WriteAllText(Path.Combine(DataDirectory, fileName), json, Utf8NoBom);
                }
            }
        }

        private static string ResolveDataDirectory()
        {
            string localDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string appDataDir = GetAppDataDirectory();

            string localSettings = Path.Combine(localDir, SettingsFileName);
            string appDataSettings = Path.Combine(appDataDir, SettingsFileName);

            if (File.Exists(localSettings) && CanWriteToDirectory(localDir))
            {
                return localDir;
            }

            if (File.Exists(appDataSettings))
            {
                return appDataDir;
            }

            if (CanWriteToDirectory(localDir))
            {
                return localDir;
            }

            return appDataDir;
        }

        private static string GetAppDataDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppFolderName);
        }

        private static bool CanWriteToDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return false;
                }

                string probeFile = Path.Combine(directory, $".write_probe_{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probeFile, string.Empty);
                File.Delete(probeFile);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void MigrateDirectoryIfNeeded(string sourceDir, string targetDir)
        {
            EnsureDirectoryExists(targetDir);
            string[] knownFiles =
            {
                SettingsFileName,
                IgnoredWordsFileName,
                UserDictionaryFileName,
                AutoReplaceFileName,
                AppBlacklistFileName
            };

            foreach (string file in knownFiles)
            {
                string src = Path.Combine(sourceDir, file);
                string dst = Path.Combine(targetDir, file);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    try
                    {
                        File.Copy(src, dst, overwrite: false);
                    }
                    catch
                    {
                        // Игнорируем сбои копирования отдельных файлов при миграции
                    }
                }
            }
        }

        private static void EnsureDirectoryExists(string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }
}
