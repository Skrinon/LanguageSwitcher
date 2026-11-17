using System;
using System.Threading;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Отслеживает счётчики исправлений и расчёт сэкономленного времени,
    /// сохраняя данные в statistics.json с отложенной записью (debounce 2 сек).
    /// </summary>
    public sealed class UsageStatisticsService : IDisposable
    {
        private const int SaveDebounceMs = 2000;

        private readonly SettingsManager _settingsManager;
        private readonly object _lock = new object();
        private readonly Timer _saveTimer;

        private UsageStatisticsData _data;
        private bool _hasPendingSave;
        private bool _disposed;

        public event EventHandler<UsageStatisticsData>? StatisticsChanged;

        public UsageStatisticsService(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _data = _settingsManager.LoadStatistics();
            _saveTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }

        public UsageStatisticsData GetSnapshot()
        {
            lock (_lock)
            {
                return _data.Clone();
            }
        }

        /// <summary>
        /// Фиксирует переключение раскладки (автоматическое или ручное) и возвращает начисленные секунды экономии.
        /// </summary>
        public double RecordLayoutSwitch(int wordLength)
        {
            double saved = 1.5 + (Math.Max(1, wordLength) * 0.22);
            UsageStatisticsData snapshot;

            lock (_lock)
            {
                _data.LayoutSwitchesCount++;
                _data.EstimatedTimeSavedSeconds += saved;
                snapshot = _data.Clone();
                ScheduleSaveLocked();
            }

            StatisticsChanged?.Invoke(this, snapshot);
            return saved;
        }

        /// <summary>
        /// Фиксирует исправление механической опечатки и возвращает начисленные секунды экономии.
        /// </summary>
        public double RecordTypoFix(int wordLength)
        {
            double saved = 1.5 + (Math.Max(1, wordLength) * 0.22);
            UsageStatisticsData snapshot;

            lock (_lock)
            {
                _data.TyposFixedCount++;
                _data.EstimatedTimeSavedSeconds += saved;
                snapshot = _data.Clone();
                ScheduleSaveLocked();
            }

            StatisticsChanged?.Invoke(this, snapshot);
            return saved;
        }

        /// <summary>
        /// Фиксирует исправление случайной второй заглавной буквы и возвращает начисленные секунды экономии.
        /// </summary>
        public double RecordDoubleCapitalFix()
        {
            const double saved = 1.2;
            UsageStatisticsData snapshot;

            lock (_lock)
            {
                _data.DoubleCapitalsFixedCount++;
                _data.EstimatedTimeSavedSeconds += saved;
                snapshot = _data.Clone();
                ScheduleSaveLocked();
            }

            StatisticsChanged?.Invoke(this, snapshot);
            return saved;
        }

        /// <summary>
        /// Фиксирует срабатывание пользовательской автозамены и возвращает начисленные секунды экономии.
        /// </summary>
        public double RecordAutoReplace(int triggerLength, int replacementLength)
        {
            double saved = Math.Max(0.5, (replacementLength - triggerLength) * 0.18);
            UsageStatisticsData snapshot;

            lock (_lock)
            {
                _data.AutoReplacementsCount++;
                _data.EstimatedTimeSavedSeconds += saved;
                snapshot = _data.Clone();
                ScheduleSaveLocked();
            }

            StatisticsChanged?.Invoke(this, snapshot);
            return saved;
        }

        /// <summary>
        /// Вычитает отменённое двойным Left Alt исправление и начисленное время из статистики.
        /// </summary>
        public void RevertAutoAction(AutoActionRecord? record)
        {
            if (record == null || record.ActionType == AutoActionType.None)
            {
                return;
            }

            UsageStatisticsData snapshot;
            lock (_lock)
            {
                switch (record.ActionType)
                {
                    case AutoActionType.AutoLayoutSwitch:
                        if (_data.LayoutSwitchesCount > 0) _data.LayoutSwitchesCount--;
                        break;
                    case AutoActionType.TypoCorrection:
                        if (_data.TyposFixedCount > 0) _data.TyposFixedCount--;
                        break;
                    case AutoActionType.DoubleCapitalCorrection:
                        if (_data.DoubleCapitalsFixedCount > 0) _data.DoubleCapitalsFixedCount--;
                        break;
                    case AutoActionType.CustomAutoReplace:
                        if (_data.AutoReplacementsCount > 0) _data.AutoReplacementsCount--;
                        break;
                }

                _data.EstimatedTimeSavedSeconds = Math.Max(
                    0.0,
                    _data.EstimatedTimeSavedSeconds - Math.Max(0.0, record.TimeSavedSeconds));

                snapshot = _data.Clone();
                ScheduleSaveLocked();
            }

            StatisticsChanged?.Invoke(this, snapshot);
        }

        /// <summary>
        /// Сбрасывает всю статистику к нулю и немедленно сохраняет на диск.
        /// </summary>
        public void Reset()
        {
            UsageStatisticsData snapshot;
            lock (_lock)
            {
                _data = new UsageStatisticsData();
                _hasPendingSave = false;
                _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                snapshot = _data.Clone();
                _settingsManager.SaveStatistics(_data);
            }

            StatisticsChanged?.Invoke(this, snapshot);
        }

        /// <summary>
        /// Немедленно сбрасывает отложенные изменения статистики в statistics.json.
        /// </summary>
        public void Flush()
        {
            UsageStatisticsData? toSave = null;
            lock (_lock)
            {
                if (_hasPendingSave)
                {
                    _hasPendingSave = false;
                    _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    toSave = _data.Clone();
                }
            }

            if (toSave != null)
            {
                _settingsManager.SaveStatistics(toSave);
            }
        }

        /// <summary>
        /// Форматирует сэкономленное время в человекочитаемый локализованный вид («45 сек», «14 мин 20 сек», «2 ч 15 мин»).
        /// </summary>
        public static string FormatTimeSaved(LocalizationService loc, double totalSeconds, bool compact = false)
        {
            long rounded = Math.Max(0, (long)Math.Round(totalSeconds));
            string secUnit = loc.GetString("StatSec");
            string minUnit = loc.GetString("StatMin");
            string hourUnit = loc.GetString("StatHour");

            if (rounded < 60)
            {
                return $"{rounded} {secUnit}";
            }

            long totalMinutes = rounded / 60;
            long remainingSeconds = rounded % 60;

            if (totalMinutes < 60)
            {
                if (compact || remainingSeconds == 0)
                {
                    return $"{totalMinutes} {minUnit}";
                }

                return $"{totalMinutes} {minUnit} {remainingSeconds} {secUnit}";
            }

            long hours = totalMinutes / 60;
            long remainingMinutes = totalMinutes % 60;
            if (remainingMinutes == 0)
            {
                return $"{hours} {hourUnit}";
            }

            return $"{hours} {hourUnit} {remainingMinutes} {minUnit}";
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Flush();
            _saveTimer.Dispose();
            _disposed = true;
        }

        private void ScheduleSaveLocked()
        {
            _hasPendingSave = true;
            _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
        }

        private void OnDebounceTimerElapsed(object? state)
        {
            Flush();
        }
    }
}
