using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Снимок содержимого буфера обмена пользователя для последующего восстановления.
    /// </summary>
    public sealed class ClipboardSnapshot
    {
        public Dictionary<string, object> Formats { get; } = new Dictionary<string, object>();
        public string? PlainTextFallback { get; set; }
        public bool HasContent => Formats.Count > 0 || !string.IsNullOrEmpty(PlainTextFallback);
    }

    /// <summary>
    /// Управляет безопасным сохранением, чтением, записью и восстановлением буфера обмена Windows
    /// с автоматическими повторами при кратковременной блокировке буфера (COMException).
    /// </summary>
    public sealed class ClipboardManager
    {
        private const int MaxRetries = 6;
        private const int RetryDelayMs = 15;

        /// <summary>
        /// Сохраняет текущее содержимое буфера обмена (текст и поддерживаемые форматы данных).
        /// </summary>
        public ClipboardSnapshot SaveClipboard()
        {
            var snapshot = new ClipboardSnapshot();

            ExecuteWithRetry(() =>
            {
                if (Clipboard.ContainsText())
                {
                    snapshot.PlainTextFallback = Clipboard.GetText();
                }

                IDataObject? dataObject = Clipboard.GetDataObject();
                if (dataObject == null)
                {
                    return;
                }

                string[] formats = dataObject.GetFormats(autoConvert: false);
                foreach (string format in formats)
                {
                    // Пропускаем внутренние OLE-форматы, которые нельзя безопасно сериализовать между снимками
                    if (string.IsNullOrWhiteSpace(format)
                        || format.StartsWith("Object", StringComparison.OrdinalIgnoreCase)
                        || format.Equals("EnhancedMetafile", StringComparison.OrdinalIgnoreCase)
                        || format.Equals("MetaFilePict", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        object? data = dataObject.GetData(format, autoConvert: false);
                        if (data != null)
                        {
                            snapshot.Formats[format] = data;
                        }
                    }
                    catch
                    {
                        // Игнорируем отдельные нестандартные форматы, чтобы не потерять остальные данные
                    }
                }
            });

            return snapshot;
        }

        /// <summary>
        /// Очищает буфер обмена перед имитацией Ctrl+C, чтобы точно определить наличие выделения.
        /// </summary>
        public void ClearClipboard()
        {
            ExecuteWithRetry(Clipboard.Clear);
        }

        /// <summary>
        /// Читает текст из буфера обмена (приоритетно в кодировке Unicode).
        /// </summary>
        public string GetText()
        {
            string result = string.Empty;

            ExecuteWithRetry(() =>
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    result = Clipboard.GetText(TextDataFormat.UnicodeText);
                }
                else if (Clipboard.ContainsText())
                {
                    result = Clipboard.GetText();
                }
            });

            return result;
        }

        /// <summary>
        /// Помещает конвертированную строку в буфер обмена.
        /// </summary>
        public bool SetText(string text)
        {
            return ExecuteWithRetry(() =>
            {
                Clipboard.SetDataObject(text ?? string.Empty, copy: true);
            });
        }

        /// <summary>
        /// Восстанавливает ранее сохранённое содержимое буфера обмена пользователя.
        /// </summary>
        public void RestoreClipboard(ClipboardSnapshot? snapshot)
        {
            if (snapshot == null || !snapshot.HasContent)
            {
                ExecuteWithRetry(Clipboard.Clear);
                return;
            }

            bool restored = false;

            if (snapshot.Formats.Count > 0)
            {
                var dataObject = new DataObject();
                int added = 0;

                foreach (KeyValuePair<string, object> pair in snapshot.Formats)
                {
                    try
                    {
                        dataObject.SetData(pair.Key, pair.Value, autoConvert: false);
                        added++;
                    }
                    catch
                    {
                        // Если конкретный формат не поддерживается при обратной записи, пропускаем его
                    }
                }

                if (added > 0)
                {
                    restored = ExecuteWithRetry(() => Clipboard.SetDataObject(dataObject, copy: true));
                }
            }

            if (!restored && snapshot.PlainTextFallback != null)
            {
                ExecuteWithRetry(() => Clipboard.SetDataObject(snapshot.PlainTextFallback, copy: true));
            }
        }

        private static bool ExecuteWithRetry(Action action)
        {
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    action();
                    return true;
                }
                catch (COMException)
                {
                    if (attempt == MaxRetries - 1)
                    {
                        return false;
                    }

                    Thread.Sleep(RetryDelayMs);
                }
                catch (ExternalException)
                {
                    if (attempt == MaxRetries - 1)
                    {
                        return false;
                    }

                    Thread.Sleep(RetryDelayMs);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }
}
