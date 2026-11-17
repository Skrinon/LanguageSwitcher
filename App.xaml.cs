using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LanguageSwitcher.Models;
using LanguageSwitcher.Services;

namespace LanguageSwitcher
{
    /// <summary>
    /// Точка входа приложения: обеспечивает запуск единственного экземпляра,
    /// инициализирует словари, локализацию, сервисы исключений/автозамен/перехвата и трей.
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\LanguageSwitcher_SingleInstance_Mutex";
        private const string ShowSettingsEventName = @"Local\LanguageSwitcher_ShowSettings_Event";
        private const int ASFW_ANY = -1;

        private Mutex? _instanceMutex;
        private EventWaitHandle? _showSettingsEvent;
        private CancellationTokenSource? _ipcCts;
        private DispatcherTimer? _exclusionPollTimer;

        private SettingsManager? _settingsManager;
        private AppSettings? _settings;
        private LocalizationService? _localizationService;
        private DictionaryService? _dictionaryService;
        private DictionaryImportService? _dictionaryImportService;
        private AutoReplaceService? _autoReplaceService;
        private AppExclusionService? _appExclusionService;
        private UsageStatisticsService? _usageStatisticsService;
        private ProfileBackupService? _profileBackupService;
        private LayoutConverter? _layoutConverter;
        private ClipboardManager? _clipboardManager;
        private KeyboardHookService? _keyboardHookService;
        private WindowInputService? _windowInputService;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                try
                {
                    AllowSetForegroundWindow(ASFW_ANY);
                    using EventWaitHandle showEvent = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                    showEvent.Set();
                }
                catch
                {
                    // Игнорируем, если событие ещё не инициализировано
                }

                Shutdown(0);
                return;
            }

            _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
            StartShowSettingsListener();

            bool startMinimized = e.Args.Any(arg =>
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));

            _settingsManager = new SettingsManager();
            _settings = _settingsManager.Load(syncStartupFromRegistry: true);

            _localizationService = new LocalizationService();
            _localizationService.ApplyLanguage(_settings.InterfaceLanguage);

            _dictionaryService = new DictionaryService(_settingsManager);
            _dictionaryImportService = new DictionaryImportService(_dictionaryService);
            _autoReplaceService = new AutoReplaceService(_settingsManager);
            _appExclusionService = new AppExclusionService(_settingsManager);
            _usageStatisticsService = new UsageStatisticsService(_settingsManager);
            _profileBackupService = new ProfileBackupService(
                _settingsManager,
                _dictionaryService,
                _autoReplaceService,
                _appExclusionService);

            _layoutConverter = new LayoutConverter();
            _clipboardManager = new ClipboardManager();
            _keyboardHookService = new KeyboardHookService(Dispatcher)
            {
                IsPaused = _settings.IsPaused
            };

            _windowInputService = new WindowInputService(
                _layoutConverter,
                _clipboardManager,
                _keyboardHookService,
                _dictionaryService,
                _autoReplaceService,
                _appExclusionService,
                _usageStatisticsService,
                _settings);

            _keyboardHookService.InputContextReset += OnInputContextReset;
            _keyboardHookService.DoubleLeftAltTriggered += OnDoubleLeftAltTriggered;
            _keyboardHookService.WordDelimiterTyped += OnWordDelimiterTyped;

            _windowInputService.ExclusionStateChanged += OnExclusionStateChanged;
            _windowInputService.AutoActionPerformed += OnAutoActionPerformed;
            _windowInputService.SmartUndoPerformed += OnSmartUndoPerformed;
            _windowInputService.IgnoredWordsModified += OnIgnoredWordsModified;

            try
            {
                _keyboardHookService.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось запустить глобальный перехват клавиатуры:\n{ex.Message}",
                    _localizationService.GetString("AppHeader"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            _mainWindow = new MainWindow(
                _settingsManager,
                _settings,
                _keyboardHookService,
                _dictionaryService,
                _dictionaryImportService,
                _autoReplaceService,
                _appExclusionService,
                _profileBackupService,
                _localizationService,
                _usageStatisticsService);
            _mainWindow.ExitRequested += OnExitRequested;
            MainWindow = _mainWindow;

            // Асинхронная загрузка встроенных словарей при старте без блокировки UI
            _ = LoadDictionariesAsync();

            StartExclusionStatusMonitor();

            if (!startMinimized)
            {
                _mainWindow.ShowAndActivate();
            }
        }

        private async Task LoadDictionariesAsync()
        {
            if (_dictionaryService == null)
            {
                return;
            }

            try
            {
                await _dictionaryService.LoadBuiltInDictionariesAsync();
                await Dispatcher.InvokeAsync(() => _mainWindow?.RefreshBuiltInDictionaryCounter());
            }
            catch
            {
                // Не прерываем работу при отсутствии отдельных словарных файлов
            }
        }

        private void StartExclusionStatusMonitor()
        {
            _exclusionPollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _exclusionPollTimer.Tick += (_, _) =>
            {
                if (_settings == null || _appExclusionService == null || _mainWindow == null)
                {
                    return;
                }

                if (!_settings.EnableAppExclusions)
                {
                    _mainWindow.UpdateExclusionState(ExclusionCheckResult.Active);
                    return;
                }

                ExclusionCheckResult result = _appExclusionService.EvaluateForegroundWindow(_settings);
                _mainWindow.UpdateExclusionState(result);
            };
            _exclusionPollTimer.Start();
        }

        private void StartShowSettingsListener()
        {
            if (_showSettingsEvent == null)
            {
                return;
            }

            _ipcCts = new CancellationTokenSource();
            CancellationToken token = _ipcCts.Token;

            Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    int index = WaitHandle.WaitAny(new WaitHandle[] { _showSettingsEvent, token.WaitHandle });
                    if (index == 0)
                    {
                        Dispatcher.BeginInvoke(new Action(() => _mainWindow?.ShowAndActivate()));
                    }
                    else
                    {
                        break;
                    }
                }
            }, token);
        }

        private void OnInputContextReset(object? sender, EventArgs e)
        {
            _windowInputService?.ResetCycleState();
        }

        private async void OnWordDelimiterTyped(object? sender, WordDelimiterEventArgs e)
        {
            if (_settings == null || _settings.IsPaused || _windowInputService == null)
            {
                return;
            }

            try
            {
                await _windowInputService.ProcessWordDelimiterAsync(e, _settings);
            }
            catch
            {
                // Не прерываем фоновый сервис при сбое взаимодействия с отдельным окном
            }
        }

        private async void OnDoubleLeftAltTriggered(object? sender, EventArgs e)
        {
            if (_settings == null || _settings.IsPaused || _windowInputService == null)
            {
                return;
            }

            try
            {
                ConversionResult? result = await _windowInputService.ProcessTriggerAsync(_settings);
                if (result != null && result.Success && _mainWindow != null)
                {
                    _mainWindow.UpdateLastConversionStatus(result);
                }
            }
            catch
            {
                // Не прерываем работу фонового сервиса при сбоях взаимодействия с отдельными окнами
            }
        }

        private void OnExclusionStateChanged(object? sender, ExclusionCheckResult e)
        {
            _mainWindow?.UpdateExclusionState(e);
        }

        private void OnAutoActionPerformed(object? sender, AutoActionRecord e)
        {
            _mainWindow?.UpdateLastAutoActionStatus(e);
        }

        private void OnSmartUndoPerformed(object? sender, AutoActionRecord e)
        {
            _mainWindow?.UpdateSmartUndoStatus(e);
        }

        private void OnIgnoredWordsModified(object? sender, EventArgs e)
        {
            _mainWindow?.RefreshExceptionsFromExternalChange();
        }

        private void OnExitRequested(object? sender, EventArgs e)
        {
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _exclusionPollTimer?.Stop();
            _ipcCts?.Cancel();
            _ipcCts?.Dispose();
            _showSettingsEvent?.Dispose();

            if (_keyboardHookService != null)
            {
                _keyboardHookService.InputContextReset -= OnInputContextReset;
                _keyboardHookService.DoubleLeftAltTriggered -= OnDoubleLeftAltTriggered;
                _keyboardHookService.WordDelimiterTyped -= OnWordDelimiterTyped;
                _keyboardHookService.Dispose();
            }

            if (_windowInputService != null)
            {
                _windowInputService.ExclusionStateChanged -= OnExclusionStateChanged;
                _windowInputService.AutoActionPerformed -= OnAutoActionPerformed;
                _windowInputService.SmartUndoPerformed -= OnSmartUndoPerformed;
                _windowInputService.IgnoredWordsModified -= OnIgnoredWordsModified;
            }

            _mainWindow?.DisposeTray();
            _usageStatisticsService?.Dispose();

            if (_instanceMutex != null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch
                {
                    // Мьютекс мог не принадлежать потоку при раннем завершении
                }

                _instanceMutex.Dispose();
            }

            base.OnExit(e);
        }

        /// <summary>
        /// Разрешает уже запущенному экземпляру перевести своё окно настроек на передний план Windows.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
    }
}
