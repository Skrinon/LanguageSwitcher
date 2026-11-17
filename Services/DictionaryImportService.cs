using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Статистика результата импорта пользовательского словаря.
    /// </summary>
    public sealed class DictionaryImportResult
    {
        public int AddedCount { get; }
        public int SkippedDuplicatesCount { get; }
        public int IgnoredInvalidCount { get; }
        public string DetectedEncodingName { get; }

        public DictionaryImportResult(
            int addedCount,
            int skippedDuplicatesCount,
            int ignoredInvalidCount,
            string detectedEncodingName)
        {
            AddedCount = addedCount;
            SkippedDuplicatesCount = skippedDuplicatesCount;
            IgnoredInvalidCount = ignoredInvalidCount;
            DetectedEncodingName = detectedEncodingName;
        }
    }

    /// <summary>
    /// Асинхронный импортёр словарей из форматов .txt, .dic (Hunspell) и .csv
    /// с автоопределением кодировки (UTF-8 / UTF-8 BOM / Windows-1251) и умным разбором строк.
    /// </summary>
    public sealed class DictionaryImportService
    {
        private readonly DictionaryService _dictionaryService;

        static DictionaryImportService()
        {
            // Регистрируем кодовые страницы для поддержки Windows-1251 в .NET 8
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public DictionaryImportService(DictionaryService dictionaryService)
        {
            _dictionaryService = dictionaryService ?? throw new ArgumentNullException(nameof(dictionaryService));
        }

        /// <summary>
        /// Импортирует файл словаря в user_dictionary.json с привязкой к указанному KLID языка.
        /// </summary>
        public async Task<DictionaryImportResult> ImportFileAsync(
            string filePath,
            string targetLanguageKlid,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("Файл словаря не найден.", filePath);
            }

            return await Task.Run(() =>
            {
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                Encoding encoding = DetectEncoding(filePath);
                long fileLength = new FileInfo(filePath).Length;

                var parsedWords = new List<string>();
                var seenInFile = new HashSet<string>(StringComparer.Ordinal);
                int duplicatesInFile = 0;
                int invalidCount = 0;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);

                int lineNumber = 0;
                int lastReportedPercent = -1;

                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? rawLine = reader.ReadLine();
                    lineNumber++;

                    if (rawLine == null)
                    {
                        continue;
                    }

                    string trimmed = rawLine.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        continue;
                    }

                    // Первая строка Hunspell .dic файлов обычно содержит число слов в словаре
                    if (lineNumber == 1 && extension == ".dic" && int.TryParse(trimmed, out _))
                    {
                        continue;
                    }

                    // Пропускаем комментарии
                    if (trimmed.StartsWith("#", StringComparison.Ordinal)
                        || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string? extracted = ExtractWordFromLine(trimmed, extension);
                    if (string.IsNullOrEmpty(extracted))
                    {
                        invalidCount++;
                        continue;
                    }

                    if (seenInFile.Add(extracted))
                    {
                        parsedWords.Add(extracted);
                    }
                    else
                    {
                        duplicatesInFile++;
                    }

                    if (progress != null && fileLength > 0 && lineNumber % 2000 == 0)
                    {
                        int percent = (int)Math.Clamp((stream.Position * 90L) / fileLength, 0, 90);
                        if (percent != lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress.Report(percent);
                        }
                    }
                }

                (int addedCount, int duplicatesWithExisting) = _dictionaryService.AddUserWords(parsedWords, targetLanguageKlid);
                progress?.Report(100);

                return new DictionaryImportResult(
                    addedCount,
                    duplicatesInFile + duplicatesWithExisting,
                    invalidCount,
                    encoding.EncodingName);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Автоматически определяет кодировку файла: UTF-8 BOM, UTF-16, чистый UTF-8 или Windows-1251.
        /// </summary>
        public static Encoding DetectEncoding(string filePath)
        {
            byte[] sample = new byte[Math.Min(new FileInfo(filePath).Length, 256 * 1024)];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int totalRead = 0;
                while (totalRead < sample.Length)
                {
                    int read = fs.Read(sample, totalRead, sample.Length - totalRead);
                    if (read == 0)
                    {
                        break;
                    }

                    totalRead += read;
                }

                if (totalRead < sample.Length)
                {
                    Array.Resize(ref sample, totalRead);
                }
            }

            if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            }

            if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }

            try
            {
                var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                strictUtf8.GetString(sample);
                return strictUtf8;
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(1251);
            }
        }

        private static string? ExtractWordFromLine(string line, string extension)
        {
            string candidate = line;

            // Для CSV и таблиц отделяем первую колонку (разделители: запятая, точка с запятой, табуляция, пробел)
            if (extension == ".csv")
            {
                int sep = candidate.IndexOfAny(new[] { ',', ';', '\t' });
                if (sep >= 0)
                {
                    candidate = candidate.Substring(0, sep);
                }
            }
            else
            {
                int ws = candidate.IndexOfAny(new[] { ' ', '\t', ',', ';' });
                if (ws >= 0)
                {
                    candidate = candidate.Substring(0, ws);
                }
            }

            // Для Hunspell .dic убираем флаги аффиксов после '/'
            int slashIdx = candidate.IndexOf('/');
            if (slashIdx >= 0)
            {
                candidate = candidate.Substring(0, slashIdx);
            }

            candidate = candidate
                .Trim()
                .Trim('"', '\u00AB', '\u00BB')
                .Replace('\u2019', '\'')
                .Replace('\u02BC', '\'')
                .ToLowerInvariant();

            if (candidate.Length == 0 || candidate.Length > 64)
            {
                return null;
            }

            // Отбрасываем числа, строки с цифрами и мусор без букв
            bool hasLetter = false;
            for (int i = 0; i < candidate.Length; i++)
            {
                char ch = candidate[i];
                if (char.IsLetter(ch))
                {
                    hasLetter = true;
                }
                else if (ch == '-' || ch == '\'')
                {
                    // Дефис и апостроф допустимы только внутри слова (не на краях и не подряд)
                    if (i == 0 || i == candidate.Length - 1)
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            return hasLetter ? candidate : null;
        }
    }
}
