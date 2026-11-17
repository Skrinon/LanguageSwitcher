using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Метаданные доступного файла локализации из папки locales/.
    /// </summary>
    public sealed class LocaleMetadata
    {
        public string LanguageCode { get; }
        public string LanguageName { get; }
        public bool IsRtl { get; }
        public string FilePath { get; }

        public LocaleMetadata(string languageCode, string languageName, bool isRtl, string filePath)
        {
            LanguageCode = languageCode;
            LanguageName = languageName;
            IsRtl = isRtl;
            FilePath = filePath;
        }
    }

    internal sealed class LocaleFilePayload
    {
        public string? languageCode { get; set; }
        public string? languageName { get; set; }
        public bool isRtl { get; set; }
        public Dictionary<string, string>? strings { get; set; }
    }

    /// <summary>
    /// Сервис динамической локализации интерфейса на основе WPF DynamicResource.
    /// Сканирует папку locales/, обновляет ресурсы приложения без перезапуска и поддерживает RTL-языки.
    /// </summary>
    public sealed class LocalizationService
    {
        private const string EmbeddedLocalePrefix = "LanguageSwitcher.locales.";
        private const string EmbeddedLocaleSuffix = ".json";

        private readonly string _localesDirectory;
        private readonly bool _isDefaultDirectory;
        private readonly Dictionary<string, LocaleFilePayload> _loadedPayloads =
            new Dictionary<string, LocaleFilePayload>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _fallbackStrings =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private Dictionary<string, string> _activeStrings =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<LocaleMetadata> AvailableLocales { get; private set; } = Array.Empty<LocaleMetadata>();
        public string CurrentLanguageCode { get; private set; } = "en";
        public bool IsRtl { get; private set; }

        public event EventHandler? LanguageChanged;

        public LocalizationService(string? customLocalesDirectory = null)
        {
            _isDefaultDirectory = string.IsNullOrWhiteSpace(customLocalesDirectory);
            _localesDirectory = _isDefaultDirectory
                ? Path.Combine(AppContext.BaseDirectory, "locales")
                : customLocalesDirectory!;

            ScanLocales();
        }

        /// <summary>
        /// Сканирует все JSON-файлы в папке locales/ (и встроенные ресурсы) и загружает резервный английский словарь.
        /// </summary>
        public void ScanLocales()
        {
            _loadedPayloads.Clear();
            var metadataByCode = new Dictionary<string, LocaleMetadata>(StringComparer.OrdinalIgnoreCase);

            if (_isDefaultDirectory)
            {
                var assembly = typeof(LocalizationService).Assembly;
                foreach (string resourceName in assembly.GetManifestResourceNames()
                    .Where(n => n.StartsWith(EmbeddedLocalePrefix, StringComparison.OrdinalIgnoreCase) &&
                                n.EndsWith(EmbeddedLocaleSuffix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                        if (stream == null)
                        {
                            continue;
                        }

                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        string json = reader.ReadToEnd();
                        string fallbackCode = resourceName.Substring(
                            EmbeddedLocalePrefix.Length,
                            resourceName.Length - EmbeddedLocalePrefix.Length - EmbeddedLocaleSuffix.Length);

                        RegisterLocaleJson(json, fallbackCode, $"embedded://{resourceName}", metadataByCode);
                    }
                    catch
                    {
                        // Пропускаем повреждённый встроенный ресурс локализации
                    }
                }
            }

            if (Directory.Exists(_localesDirectory))
            {
                foreach (string file in Directory.GetFiles(_localesDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        string json = File.ReadAllText(file, Encoding.UTF8);
                        RegisterLocaleJson(json, Path.GetFileNameWithoutExtension(file), file, metadataByCode);
                    }
                    catch
                    {
                        // Пропускаем повреждённый файл локализации, не останавливая запуск
                    }
                }
            }

            if (_loadedPayloads.TryGetValue("en", out LocaleFilePayload? enPayload) && enPayload.strings != null)
            {
                _fallbackStrings = new Dictionary<string, string>(enPayload.strings, StringComparer.Ordinal);
            }

            // Упорядочиваем список языков: английский, украинский, русский, затем остальные по алфавиту
            string[] preferredOrder = { "en", "uk", "ru" };
            AvailableLocales = metadataByCode.Values
                .OrderBy(item =>
                {
                    int idx = Array.IndexOf(preferredOrder, item.LanguageCode.ToLowerInvariant());
                    return idx >= 0 ? idx : 100;
                })
                .ThenBy(item => item.LanguageName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private void RegisterLocaleJson(
            string json,
            string fallbackCode,
            string sourcePath,
            Dictionary<string, LocaleMetadata> metadataByCode)
        {
            LocaleFilePayload? payload = JsonSerializer.Deserialize<LocaleFilePayload>(json);
            if (payload == null || payload.strings == null)
            {
                return;
            }

            string code = string.IsNullOrWhiteSpace(payload.languageCode)
                ? fallbackCode
                : payload.languageCode.Trim();
            string name = string.IsNullOrWhiteSpace(payload.languageName) ? code : payload.languageName.Trim();

            _loadedPayloads[code] = payload;
            metadataByCode[code] = new LocaleMetadata(code, name, payload.isRtl, sourcePath);
        }

        /// <summary>
        /// Применяет выбранный язык интерфейса и обновляет все ключи DynamicResource в окнах WPF.
        /// </summary>
        public void ApplyLanguage(string? languageCode)
        {
            string targetCode = string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode.Trim();

            if (!_loadedPayloads.TryGetValue(targetCode, out LocaleFilePayload? payload))
            {
                targetCode = _loadedPayloads.ContainsKey("ru")
                    ? "ru"
                    : (_loadedPayloads.ContainsKey("en") ? "en" : targetCode);
                _loadedPayloads.TryGetValue(targetCode, out payload);
            }

            CurrentLanguageCode = targetCode;
            IsRtl = payload?.isRtl ?? false;

            var merged = new Dictionary<string, string>(_fallbackStrings, StringComparer.Ordinal);
            if (payload?.strings != null)
            {
                foreach (KeyValuePair<string, string> pair in payload.strings)
                {
                    if (!string.IsNullOrEmpty(pair.Value))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            _activeStrings = merged;

            if (Application.Current != null)
            {
                ResourceDictionary resources = Application.Current.Resources;
                foreach (KeyValuePair<string, string> pair in merged)
                {
                    resources[$"Loc.{pair.Key}"] = pair.Value;
                }
            }

            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Возвращает локализованную строку по ключу (с фолбэком на английский язык).
        /// </summary>
        public string GetString(string key)
        {
            if (_activeStrings.TryGetValue(key, out string? value))
            {
                return value;
            }

            if (_fallbackStrings.TryGetValue(key, out string? fallback))
            {
                return fallback;
            }

            return key;
        }

        /// <summary>
        /// Форматирует локализованную строку с параметрами.
        /// </summary>
        public string Format(string key, params object[] args)
        {
            string template = GetString(key);
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
            }
        }
    }
}
