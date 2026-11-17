using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanguageSwitcher.Models;
using LanguageSwitcher.Services;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LanguageSwitcher
{
    internal sealed class LanguageFilterOption
    {
        public SupportedLanguage? Language { get; }
        public string Label { get; }

        public LanguageFilterOption(SupportedLanguage? language, string label)
        {
            Language = language;
            Label = label;
        }
    }

    /// <summary>
    /// Компактное 5-вкладочное окно настроек в стиле Windows 11 с динамической локализацией
    /// и управлением иконкой в системном трее.
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;
        private const int SW_RESTORE = 9;

        private readonly SettingsManager _settingsManager;
        private readonly AppSettings _settings;
        private readonly KeyboardHookService _hookService;
        private readonly DictionaryService _dictionaryService;
        private readonly DictionaryImportService _dictionaryImportService;
        private readonly AutoReplaceService _autoReplaceService;
        private readonly AppExclusionService _appExclusionService;
        private readonly ProfileBackupService _profileBackupService;
        private readonly LocalizationService _localizationService;
        private readonly UsageStatisticsService _statisticsService;

        private Forms.NotifyIcon? _notifyIcon;
        private Forms.ToolStripMenuItem? _openMenuItem;
        private Forms.ToolStripMenuItem? _pauseMenuItem;
        private Forms.ToolStripMenuItem? _exitMenuItem;
        private Drawing.Icon? _activeTrayIcon;
        private Drawing.Icon? _pausedTrayIcon;

        private bool _isInitializingUi;
        private bool _allowExit;
        private bool _balloonShownOnce;
        private string? _editingAutoReplaceTrigger;
        private ExclusionCheckResult _latestExclusionState = ExclusionCheckResult.Active;

        public event EventHandler? ExitRequested;

        public MainWindow(
            SettingsManager settingsManager,
            AppSettings settings,
            KeyboardHookService hookService,
            DictionaryService dictionaryService,
            DictionaryImportService dictionaryImportService,
            AutoReplaceService autoReplaceService,
            AppExclusionService appExclusionService,
            ProfileBackupService profileBackupService,
            LocalizationService localizationService,
            UsageStatisticsService statisticsService)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _hookService = hookService ?? throw new ArgumentNullException(nameof(hookService));
            _dictionaryService = dictionaryService ?? throw new ArgumentNullException(nameof(dictionaryService));
            _dictionaryImportService = dictionaryImportService ?? throw new ArgumentNullException(nameof(dictionaryImportService));
            _autoReplaceService = autoReplaceService ?? throw new ArgumentNullException(nameof(autoReplaceService));
            _appExclusionService = appExclusionService ?? throw new ArgumentNullException(nameof(appExclusionService));
            _profileBackupService = profileBackupService ?? throw new ArgumentNullException(nameof(profileBackupService));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
            _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));

            _localizationService.LanguageChanged += OnLanguageChanged;
            _statisticsService.StatisticsChanged += OnStatisticsChanged;

            InitializeComponent();
            InitializeUiFromSettings();
            RefreshAllLists();
            InitializeTrayIcon();
            ApplyLocalizedTexts();
            RefreshStatisticsDisplay();
            UpdateStatusVisuals();
        }

        public void ShowAndActivate()
        {
            ShowInTaskbar = true;

            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            Activate();
            Topmost = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => Topmost = false));
            Focus();
        }

        public void ShowTrayStartupBalloon()
        {
            _notifyIcon?.ShowBalloonTip(
                2500,
                _localizationService.GetString("AppHeader"),
                _localizationService.GetString("TrayStartupBalloon"),
                Forms.ToolTipIcon.Info);
        }

        public void UpdateLastConversionStatus(ConversionResult result)
        {
            LanguageInfo src = LanguageCatalog.GetInfo(result.SourceLanguage);
            LanguageInfo dst = LanguageCatalog.GetInfo(result.TargetLanguage);
            LastActionText.Text = _localizationService.Format(
                "LastActionFormat",
                src.ShortCode,
                dst.ShortCode,
                result.ConvertedText);
        }

        public void UpdateLastAutoActionStatus(AutoActionRecord record)
        {
            switch (record.ActionType)
            {
                case AutoActionType.AutoLayoutSwitch:
                    LastActionText.Text = _localizationService.Format(
                        "LastAutoSwitchFormat",
                        LanguageCatalog.GetInfo(record.OriginalLanguage).ShortCode,
                        LanguageCatalog.GetInfo(record.TargetLanguage).ShortCode,
                        record.ReplacedText);
                    break;
                case AutoActionType.TypoCorrection:
                    LastActionText.Text = _localizationService.Format(
                        "LastTypoFixFormat",
                        record.OriginalWord,
                        record.ReplacedText);
                    break;
                case AutoActionType.DoubleCapitalCorrection:
                    LastActionText.Text = _localizationService.Format(
                        "LastDoubleCapFixFormat",
                        record.OriginalWord,
                        record.ReplacedText);
                    break;
                case AutoActionType.CustomAutoReplace:
                    LastActionText.Text = _localizationService.Format(
                        "LastAutoReplaceFormat",
                        record.OriginalWord,
                        record.ReplacedText.Replace("\r\n", " ").Replace("\n", " "));
                    break;
            }
        }

        public void UpdateSmartUndoStatus(AutoActionRecord revertedRecord)
        {
            LastActionText.Text = _localizationService.Format("LastUndoFormat", revertedRecord.OriginalWord);
            RefreshExceptionsList();
        }

        public void UpdateExclusionState(ExclusionCheckResult exclusion)
        {
            _latestExclusionState = exclusion ?? ExclusionCheckResult.Active;
            UpdateStatusVisuals();
        }

        public void RefreshBuiltInDictionaryCounter()
        {
            RefreshUserDictionaryList();
        }

        public void RefreshExceptionsFromExternalChange()
        {
            RefreshExceptionsList();
        }

        public void DisposeTray()
        {
            _allowExit = true;
            _localizationService.LanguageChanged -= OnLanguageChanged;
            _statisticsService.StatisticsChanged -= OnStatisticsChanged;

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _activeTrayIcon?.Dispose();
            _activeTrayIcon = null;

            _pausedTrayIcon?.Dispose();
            _pausedTrayIcon = null;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyWindows11RoundedCorners();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                HideToTray();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowExit)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            base.OnClosing(e);
        }

        private void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();

            if (!_balloonShownOnce)
            {
                _balloonShownOnce = true;
                ShowTrayStartupBalloon();
            }
        }

        private void InitializeUiFromSettings()
        {
            _isInitializingUi = true;
            try
            {
                InterfaceLanguageComboBox.ItemsSource = _localizationService.AvailableLocales;
                InterfaceLanguageComboBox.SelectedValue = _localizationService.CurrentLanguageCode;

                DefaultSourceComboBox.ItemsSource = LanguageCatalog.All;
                DefaultSourceComboBox.SelectedValue = _settings.DefaultSourceLanguage;
                UseDefaultSourceCheckBox.IsChecked = _settings.UseDefaultSourceLanguage;
                DefaultSourceComboBox.IsEnabled = _settings.UseDefaultSourceLanguage;

                TargetEnglishCheckBox.IsChecked = _settings.TargetLanguages.Contains(SupportedLanguage.English);
                TargetRussianCheckBox.IsChecked = _settings.TargetLanguages.Contains(SupportedLanguage.Russian);
                TargetUkrainianCheckBox.IsChecked = _settings.TargetLanguages.Contains(SupportedLanguage.Ukrainian);

                AutoSwitchLayoutCheckBox.IsChecked = _settings.AutoSwitchLayout;
                AutoCorrectTyposCheckBox.IsChecked = _settings.AutoCorrectTypos;
                IgnoreProfanityCheckBox.IsChecked = _settings.IgnoreProfanityInTypoCorrection;
                FixDoubleCapitalsCheckBox.IsChecked = _settings.FixDoubleCapitals;
                EnableCustomAutoReplaceCheckBox.IsChecked = _settings.EnableCustomAutoReplace;

                EnableAppExclusionsSyncCheckBox.IsChecked = _settings.EnableAppExclusions;
                MasterExclusionsCheckBox.IsChecked = _settings.EnableAppExclusions;
                AutoPauseFullscreenCheckBox.IsChecked = _settings.AutoPauseInFullscreen;
                BlockManualExcludedCheckBox.IsChecked = _settings.BlockManualTriggerInExcluded;

                RunAtStartupCheckBox.IsChecked = _settings.RunAtStartup;

                DictAddLanguageComboBox.ItemsSource = LanguageCatalog.All;
                DictAddLanguageComboBox.SelectedValue = SupportedLanguage.Russian;

                RebuildDictFilterOptions();
                UpdateSourceHint();
                UpdateCyclePreview();
            }
            finally
            {
                _isInitializingUi = false;
            }
        }

        private void RebuildDictFilterOptions()
        {
            SupportedLanguage? selected = DictFilterLanguageComboBox.SelectedValue as SupportedLanguage?;
            var options = new List<LanguageFilterOption>
            {
                new LanguageFilterOption(null, _localizationService.GetString("DictFilterAll"))
            };

            foreach (LanguageInfo info in LanguageCatalog.All)
            {
                options.Add(new LanguageFilterOption(info.Language, info.DisplayName));
            }

            bool prevInit = _isInitializingUi;
            _isInitializingUi = true;
            DictFilterLanguageComboBox.ItemsSource = options;
            DictFilterLanguageComboBox.SelectedValue = selected;
            if (DictFilterLanguageComboBox.SelectedIndex < 0)
            {
                DictFilterLanguageComboBox.SelectedIndex = 0;
            }
            _isInitializingUi = prevInit;
        }

        private void InitializeTrayIcon()
        {
            _activeTrayIcon = CreateTrayIcon(isPaused: false);
            _pausedTrayIcon = CreateTrayIcon(isPaused: true);

            Icon = Imaging.CreateBitmapSourceFromHIcon(
                _activeTrayIcon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            var contextMenu = new Forms.ContextMenuStrip();

            _openMenuItem = new Forms.ToolStripMenuItem(_localizationService.GetString("TrayOpenSettings"));
            _openMenuItem.Font = new Drawing.Font(_openMenuItem.Font, Drawing.FontStyle.Bold);
            _openMenuItem.Click += (_, _) => Dispatcher.Invoke(ShowAndActivate);

            _pauseMenuItem = new Forms.ToolStripMenuItem();
            _pauseMenuItem.Click += (_, _) => Dispatcher.Invoke(TogglePauseState);

            _exitMenuItem = new Forms.ToolStripMenuItem(_localizationService.GetString("TrayExit"));
            _exitMenuItem.Click += (_, _) => Dispatcher.Invoke(RequestApplicationExit);

            contextMenu.Items.Add(_openMenuItem);
            contextMenu.Items.Add(_pauseMenuItem);
            contextMenu.Items.Add(new Forms.ToolStripSeparator());
            contextMenu.Items.Add(_exitMenuItem);

            bool effectivelyPaused = _settings.IsPaused || _latestExclusionState.IsAutomaticPaused;
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = effectivelyPaused ? _pausedTrayIcon : _activeTrayIcon,
                Text = TruncateTrayText(_localizationService.GetString("TrayTooltipActive")),
                ContextMenuStrip = contextMenu,
                Visible = true
            };

            _notifyIcon.MouseClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                {
                    Dispatcher.Invoke(ShowAndActivate);
                }
            };

            _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyLocalizedTexts();
                RebuildDictFilterOptions();
                UpdateSourceHint();
                UpdateCyclePreview();
                RefreshStatisticsDisplay();
                UpdateStatusVisuals();
                RefreshAllLists();
            }));
        }

        private void OnStatisticsChanged(object? sender, UsageStatisticsData e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshStatisticsDisplay();
                UpdateStatusVisuals();
            }));
        }

        private void RefreshStatisticsDisplay()
        {
            UsageStatisticsData stats = _statisticsService.GetSnapshot();
            string timeText = UsageStatisticsService.FormatTimeSaved(_localizationService, stats.EstimatedTimeSavedSeconds, compact: false);

            StatTotalFixesText.Text = _localizationService.Format("StatTotalFixedFormat", stats.TotalFixesCount);
            StatTimeSavedText.Text = _localizationService.Format("StatTimeSavedFormat", timeText);
            StatBreakdownText.Text = _localizationService.Format(
                "StatBreakdownFormat",
                stats.LayoutSwitchesCount,
                stats.TyposFixedCount,
                stats.DoubleCapitalsFixedCount,
                stats.AutoReplacementsCount);
        }

        private void ApplyLocalizedTexts()
        {
            FlowDirection = _localizationService.IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            if (_openMenuItem != null)
            {
                _openMenuItem.Text = _localizationService.GetString("TrayOpenSettings");
            }

            if (_exitMenuItem != null)
            {
                _exitMenuItem.Text = _localizationService.GetString("TrayExit");
            }

            SaveAutoReplaceButton.Content = _editingAutoReplaceTrigger == null
                ? _localizationService.GetString("BtnAdd")
                : _localizationService.GetString("BtnSave");
        }

        private void TogglePauseState()
        {
            _settings.IsPaused = !_settings.IsPaused;
            _hookService.IsPaused = _settings.IsPaused;
            _settingsManager.Save(_settings);
            UpdateStatusVisuals();
        }

        private void RequestApplicationExit()
        {
            _allowExit = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateStatusVisuals()
        {
            bool manualPaused = _settings.IsPaused;
            bool autoExcluded = !manualPaused && _settings.EnableAppExclusions && _latestExclusionState.IsAutomaticPaused;
            bool isPausedVisual = manualPaused || autoExcluded;

            if (manualPaused)
            {
                SetBadgeColors(0xF3, 0xF4, 0xF6, 0xD1, 0xD5, 0xDB, 0x6B, 0x72, 0x80, 0x4B, 0x55, 0x63);
                StatusBadgeText.Text = _localizationService.GetString("StatusPaused");
                PauseToggleButton.Content = _localizationService.GetString("BtnResume");
            }
            else if (autoExcluded)
            {
                SetBadgeColors(0xFF, 0xF7, 0xED, 0xFD, 0xBA, 0x74, 0xEA, 0x58, 0x0C, 0x9A, 0x34, 0x12);
                string reason = !string.IsNullOrWhiteSpace(_latestExclusionState.ProcessName) && _latestExclusionState.IsBlacklisted
                    ? _latestExclusionState.ProcessName!
                    : _localizationService.GetString("StatusFullscreen");
                StatusBadgeText.Text = _localizationService.Format("StatusExcludedFormat", reason);
                PauseToggleButton.Content = _localizationService.GetString("BtnPause");
            }
            else
            {
                SetBadgeColors(0xE6, 0xF4, 0xEA, 0xB7, 0xDF, 0xC3, 0x10, 0x7C, 0x10, 0x10, 0x7C, 0x10);
                StatusBadgeText.Text = _localizationService.GetString("StatusActive");
                PauseToggleButton.Content = _localizationService.GetString("BtnPause");
            }

            if (_pauseMenuItem != null)
            {
                _pauseMenuItem.Text = manualPaused
                    ? _localizationService.GetString("TrayResume")
                    : _localizationService.GetString("TrayPause");
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Icon = isPausedVisual ? _pausedTrayIcon : _activeTrayIcon;
                string tooltip;
                if (manualPaused)
                {
                    tooltip = _localizationService.GetString("TrayTooltipPaused");
                }
                else if (autoExcluded)
                {
                    string reason = !string.IsNullOrWhiteSpace(_latestExclusionState.ProcessName) && _latestExclusionState.IsBlacklisted
                        ? _latestExclusionState.ProcessName!
                        : _localizationService.GetString("StatusFullscreen");
                    tooltip = _localizationService.Format("TrayTooltipExcludedFormat", reason);
                }
                else
                {
                    tooltip = _localizationService.GetString("TrayTooltipActive");
                }

                UsageStatisticsData stats = _statisticsService.GetSnapshot();
                string compactTime = UsageStatisticsService.FormatTimeSaved(_localizationService, stats.EstimatedTimeSavedSeconds, compact: true);
                string statsLine = _localizationService.Format("TrayStatsFormat", stats.TotalFixesCount, compactTime);
                _notifyIcon.Text = TruncateTrayText($"{tooltip}\n{statsLine}");
            }
        }

        private void SetBadgeColors(
            byte bgR, byte bgG, byte bgB,
            byte brR, byte brG, byte brB,
            byte dotR, byte dotG, byte dotB,
            byte fgR, byte fgG, byte fgB)
        {
            StatusBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(bgR, bgG, bgB));
            StatusBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(brR, brG, brB));
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(dotR, dotG, dotB));
            StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(fgR, fgG, fgB));
        }

        private void UpdateSourceHint()
        {
            if (_settings.UseDefaultSourceLanguage)
            {
                LanguageInfo info = LanguageCatalog.GetInfo(_settings.DefaultSourceLanguage);
                SourceHintText.Text = _localizationService.Format("SourceHintDefaultFormat", info.DisplayName);
            }
            else
            {
                SourceHintText.Text = _localizationService.GetString("SourceHintAuto");
            }
        }

        private void UpdateCyclePreview()
        {
            var names = new List<string>();
            foreach (SupportedLanguage lang in _settings.TargetLanguages)
            {
                names.Add(LanguageCatalog.GetInfo(lang).DisplayName);
            }

            if (names.Count == 2)
            {
                CyclePreviewText.Text = _localizationService.Format("CycleTwoFormat", names[0], names[1]);
            }
            else if (names.Count >= 3)
            {
                CyclePreviewText.Text = _localizationService.Format("CycleMultiFormat", string.Join(" → ", names), names[0]);
            }
        }

        private void RefreshAllLists()
        {
            RefreshExceptionsList();
            RefreshUserDictionaryList();
            RefreshAutoReplaceList();
            RefreshBlacklist();
        }

        #region Tab 1: Основные

        private void InterfaceLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingUi || InterfaceLanguageComboBox.SelectedValue is not string code)
            {
                return;
            }

            _settings.InterfaceLanguage = code;
            _settingsManager.Save(_settings);
            _localizationService.ApplyLanguage(code);
        }

        private void UseDefaultSourceCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi)
            {
                return;
            }

            bool isEnabled = UseDefaultSourceCheckBox.IsChecked == true;
            _settings.UseDefaultSourceLanguage = isEnabled;
            DefaultSourceComboBox.IsEnabled = isEnabled;

            UpdateSourceHint();
            _settingsManager.Save(_settings);
        }

        private void DefaultSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingUi || DefaultSourceComboBox.SelectedValue is not SupportedLanguage selectedLanguage)
            {
                return;
            }

            _settings.DefaultSourceLanguage = selectedLanguage;
            UpdateSourceHint();
            _settingsManager.Save(_settings);
        }

        private void TargetLanguageCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi)
            {
                return;
            }

            var selected = new List<SupportedLanguage>();
            if (TargetEnglishCheckBox.IsChecked == true) selected.Add(SupportedLanguage.English);
            if (TargetRussianCheckBox.IsChecked == true) selected.Add(SupportedLanguage.Russian);
            if (TargetUkrainianCheckBox.IsChecked == true) selected.Add(SupportedLanguage.Ukrainian);

            if (selected.Count < 2)
            {
                if (sender is CheckBox cb)
                {
                    _isInitializingUi = true;
                    cb.IsChecked = true;
                    _isInitializingUi = false;
                }

                TargetValidationText.Visibility = Visibility.Visible;
                return;
            }

            TargetValidationText.Visibility = Visibility.Collapsed;
            _settings.TargetLanguages = selected;
            UpdateCyclePreview();
            _settingsManager.Save(_settings);
        }

        private void AutomationOptionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi)
            {
                return;
            }

            _settings.AutoSwitchLayout = AutoSwitchLayoutCheckBox.IsChecked == true;
            _settings.AutoCorrectTypos = AutoCorrectTyposCheckBox.IsChecked == true;
            _settings.IgnoreProfanityInTypoCorrection = IgnoreProfanityCheckBox.IsChecked == true;
            _settings.FixDoubleCapitals = FixDoubleCapitalsCheckBox.IsChecked == true;
            _settings.EnableCustomAutoReplace = EnableCustomAutoReplaceCheckBox.IsChecked == true;
            _settingsManager.Save(_settings);
        }

        private void ResetStatsButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                this,
                _localizationService.GetString("ConfirmResetStats"),
                _localizationService.GetString("DialogConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _statisticsService.Reset();
            }
        }

        private void ExclusionsFlagCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi)
            {
                return;
            }

            bool newValue = sender == MasterExclusionsCheckBox
                ? MasterExclusionsCheckBox.IsChecked == true
                : EnableAppExclusionsSyncCheckBox.IsChecked == true;

            _isInitializingUi = true;
            EnableAppExclusionsSyncCheckBox.IsChecked = newValue;
            MasterExclusionsCheckBox.IsChecked = newValue;
            _isInitializingUi = false;

            _settings.EnableAppExclusions = newValue;
            _settingsManager.Save(_settings);

            if (!newValue)
            {
                _latestExclusionState = ExclusionCheckResult.Active;
            }
            else
            {
                _latestExclusionState = _appExclusionService.EvaluateForegroundWindow(_settings);
            }

            UpdateStatusVisuals();
        }

        private void RunAtStartupCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi)
            {
                return;
            }

            bool enable = RunAtStartupCheckBox.IsChecked == true;
            if (!_settingsManager.SetStartupEnabled(enable))
            {
                _isInitializingUi = true;
                RunAtStartupCheckBox.IsChecked = _settingsManager.IsStartupEnabledInRegistry();
                _isInitializingUi = false;
                return;
            }

            _settings.RunAtStartup = enable;
            _settingsManager.Save(_settings);
        }

        private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                FileName = ProfileBackupService.GetDefaultBackupFileName(),
                Filter = "JSON Backup (*.json)|*.json|All Files (*.*)|*.*",
                DefaultExt = ".json"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                _profileBackupService.ExportProfile(dialog.FileName, _settings);
                MessageBox.Show(
                    this,
                    _localizationService.GetString("BackupExportSuccess"),
                    _localizationService.GetString("DialogInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    ex.Message,
                    _localizationService.GetString("DialogErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ImportProfileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON Backup (*.json)|*.json|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            ProfileBackupData? backup = _profileBackupService.ReadAndValidateBackup(dialog.FileName);
            if (backup == null)
            {
                MessageBox.Show(
                    this,
                    _localizationService.GetString("BackupImportInvalid"),
                    _localizationService.GetString("DialogErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            MessageBoxResult choice = MessageBox.Show(
                this,
                _localizationService.GetString("BackupImportPrompt").Replace("\\n", Environment.NewLine),
                _localizationService.GetString("BackupImportTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel)
            {
                return;
            }

            ProfileImportMode mode = choice == MessageBoxResult.Yes
                ? ProfileImportMode.Merge
                : ProfileImportMode.Overwrite;

            ProfileImportSummary summary = _profileBackupService.ApplyBackup(
                backup,
                mode,
                _settings,
                _localizationService,
                syncRegistry: true);

            _hookService.IsPaused = _settings.IsPaused;
            InitializeUiFromSettings();
            RefreshAllLists();
            UpdateStatusVisuals();

            string modeLabel = _localizationService.GetString(
                mode == ProfileImportMode.Merge ? "BackupModeMerge" : "BackupModeOverwrite");

            MessageBox.Show(
                this,
                _localizationService.Format(
                    "BackupImportSuccessFormat",
                    modeLabel,
                    summary.UserDictionaryCount,
                    summary.IgnoredWordsCount,
                    summary.AutoReplaceCount,
                    summary.AppBlacklistCount),
                _localizationService.GetString("DialogInfoTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        #endregion

        #region Tab 2: Исключения

        private void RefreshExceptionsList()
        {
            string query = ExceptionSearchTextBox.Text.Trim();
            List<string> all = _dictionaryService.GetIgnoredWordsSnapshot();
            IEnumerable<string> filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(w => w.Contains(query, StringComparison.OrdinalIgnoreCase));

            ExceptionsListBox.ItemsSource = filtered.ToList();
            ExceptionsCountText.Text = _localizationService.Format("LabelCountFormat", all.Count);
        }

        private void AddExceptionsFromInput()
        {
            List<string> words = DictionaryService.ParseBulkWordInput(ExceptionInputTextBox.Text);
            if (words.Count == 0)
            {
                return;
            }

            _dictionaryService.AddIgnoredWords(words);
            ExceptionInputTextBox.Clear();
            RefreshExceptionsList();
        }

        private void AddExceptionButton_Click(object sender, RoutedEventArgs e)
        {
            AddExceptionsFromInput();
        }

        private void ExceptionInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                AddExceptionsFromInput();
            }
        }

        private void ExceptionSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshExceptionsList();
        }

        private void RemoveExceptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string word)
            {
                _dictionaryService.RemoveIgnoredWord(word);
                RefreshExceptionsList();
            }
        }

        private void ExceptionsListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ExceptionsListBox.SelectedItem is string word)
            {
                _dictionaryService.RemoveIgnoredWord(word);
                RefreshExceptionsList();
            }
        }

        private void ClearExceptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dictionaryService.GetIgnoredWordsSnapshot().Count == 0)
            {
                return;
            }

            if (MessageBox.Show(
                this,
                _localizationService.GetString("ConfirmClearExceptions"),
                _localizationService.GetString("DialogConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _dictionaryService.ClearIgnoredWords();
                RefreshExceptionsList();
            }
        }

        #endregion

        #region Tab 3: Мой словарь

        private void RefreshUserDictionaryList()
        {
            string query = DictSearchTextBox.Text.Trim();
            SupportedLanguage? filterLang = DictFilterLanguageComboBox.SelectedValue as SupportedLanguage?;

            List<UserDictionaryEntry> all = _dictionaryService.GetUserDictionarySnapshot();
            IEnumerable<UserDictionaryEntry> filtered = all;

            if (filterLang.HasValue)
            {
                filtered = filtered.Where(entry => entry.Language == filterLang.Value);
            }

            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(entry => entry.Word.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            UserDictionaryListBox.ItemsSource = filtered.ToList();
            DictCountText.Text = _localizationService.Format(
                "DictCountFormat",
                all.Count,
                _dictionaryService.GetBuiltInWordCount());
        }

        private void AddUserWordsFromInput()
        {
            if (DictAddLanguageComboBox.SelectedValue is not SupportedLanguage lang)
            {
                lang = SupportedLanguage.Russian;
            }

            List<string> words = DictionaryService.ParseBulkWordInput(DictWordInputTextBox.Text);
            if (words.Count == 0)
            {
                return;
            }

            _dictionaryService.AddUserWords(words, lang);
            DictWordInputTextBox.Clear();
            RefreshUserDictionaryList();
        }

        private void AddDictWordButton_Click(object sender, RoutedEventArgs e)
        {
            AddUserWordsFromInput();
        }

        private void DictWordInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                AddUserWordsFromInput();
            }
        }

        private async void ImportDictionaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (DictAddLanguageComboBox.SelectedValue is not SupportedLanguage targetLang)
            {
                targetLang = SupportedLanguage.Russian;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Dictionary files (*.txt;*.dic;*.csv)|*.txt;*.dic;*.csv|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            ImportDictionaryButton.IsEnabled = false;
            DictImportProgressBar.Value = 0;
            DictImportProgressBar.Visibility = Visibility.Visible;
            DictImportStatusText.Text = string.Empty;

            var progress = new Progress<int>(percent => DictImportProgressBar.Value = percent);

            try
            {
                DictionaryImportResult result = await _dictionaryImportService.ImportFileAsync(
                    dialog.FileName,
                    targetLang,
                    progress);

                RefreshUserDictionaryList();
                string summary = _localizationService.Format(
                    "DictImportSuccessFormat",
                    result.AddedCount,
                    result.SkippedDuplicatesCount);
                DictImportStatusText.Text = summary;

                MessageBox.Show(
                    this,
                    summary,
                    _localizationService.GetString("DialogInfoTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"{_localizationService.GetString("DictImportError")}\n{ex.Message}",
                    _localizationService.GetString("DialogErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                DictImportProgressBar.Visibility = Visibility.Collapsed;
                ImportDictionaryButton.IsEnabled = true;
            }
        }

        private void DictFilterLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializingUi)
            {
                RefreshUserDictionaryList();
            }
        }

        private void DictSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshUserDictionaryList();
        }

        private void RemoveDictWordButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is UserDictionaryEntry entry)
            {
                _dictionaryService.RemoveUserWord(entry.Word, entry.Language);
                RefreshUserDictionaryList();
            }
        }

        private void UserDictionaryListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && UserDictionaryListBox.SelectedItem is UserDictionaryEntry entry)
            {
                _dictionaryService.RemoveUserWord(entry.Word, entry.Language);
                RefreshUserDictionaryList();
            }
        }

        private void ClearDictButton_Click(object sender, RoutedEventArgs e)
        {
            if (_dictionaryService.GetUserWordCount() == 0)
            {
                return;
            }

            if (MessageBox.Show(
                this,
                _localizationService.GetString("ConfirmClearDictionary"),
                _localizationService.GetString("DialogConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _dictionaryService.ClearUserDictionary();
                RefreshUserDictionaryList();
            }
        }

        #endregion

        #region Tab 4: Автозамена

        private void RefreshAutoReplaceList()
        {
            string query = AutoReplaceSearchTextBox.Text.Trim();
            List<AutoReplaceRule> all = _autoReplaceService.GetRulesSnapshot();
            IEnumerable<AutoReplaceRule> filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(r => r.Trigger.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || r.Replacement.Contains(query, StringComparison.OrdinalIgnoreCase));

            AutoReplaceListBox.ItemsSource = filtered.ToList();
            AutoReplaceCountText.Text = _localizationService.Format("LabelCountFormat", all.Count);
        }

        private void SaveAutoReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            string trigger = AutoReplaceTriggerTextBox.Text.Trim();
            string replacement = AutoReplaceReplacementTextBox.Text;

            if (string.IsNullOrEmpty(trigger))
            {
                return;
            }

            _autoReplaceService.AddOrUpdateRule(
                trigger,
                replacement,
                isEnabled: true,
                originalTrigger: _editingAutoReplaceTrigger);

            ExitAutoReplaceEditMode();
            RefreshAutoReplaceList();
        }

        private void CancelAutoReplaceEditButton_Click(object sender, RoutedEventArgs e)
        {
            ExitAutoReplaceEditMode();
        }

        private void ExitAutoReplaceEditMode()
        {
            _editingAutoReplaceTrigger = null;
            AutoReplaceTriggerTextBox.Clear();
            AutoReplaceReplacementTextBox.Clear();
            SaveAutoReplaceButton.Content = _localizationService.GetString("BtnAdd");
            CancelAutoReplaceEditButton.Visibility = Visibility.Collapsed;
        }

        private void AutoReplaceSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshAutoReplaceList();
        }

        private void AutoReplaceEnabledCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Tag is AutoReplaceRule rule)
            {
                _autoReplaceService.SetRuleEnabled(rule.Trigger, cb.IsChecked == true);
            }
        }

        private void EditAutoReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoReplaceRule rule)
            {
                _editingAutoReplaceTrigger = rule.Trigger;
                AutoReplaceTriggerTextBox.Text = rule.Trigger;
                AutoReplaceReplacementTextBox.Text = rule.Replacement.Replace("\n", Environment.NewLine);
                SaveAutoReplaceButton.Content = _localizationService.GetString("BtnSave");
                CancelAutoReplaceEditButton.Visibility = Visibility.Visible;
                AutoReplaceTriggerTextBox.Focus();
            }
        }

        private void RemoveAutoReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is AutoReplaceRule rule)
            {
                if (string.Equals(_editingAutoReplaceTrigger, rule.Trigger, StringComparison.OrdinalIgnoreCase))
                {
                    ExitAutoReplaceEditMode();
                }

                _autoReplaceService.RemoveRule(rule.Trigger);
                RefreshAutoReplaceList();
            }
        }

        private void AutoReplaceListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && AutoReplaceListBox.SelectedItem is AutoReplaceRule rule)
            {
                _autoReplaceService.RemoveRule(rule.Trigger);
                RefreshAutoReplaceList();
            }
        }

        private void ClearAutoReplaceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_autoReplaceService.Count == 0)
            {
                return;
            }

            if (MessageBox.Show(
                this,
                _localizationService.GetString("ConfirmClearAutoReplace"),
                _localizationService.GetString("DialogConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                ExitAutoReplaceEditMode();
                _autoReplaceService.ClearRules();
                RefreshAutoReplaceList();
            }
        }

        #endregion

        #region Tab 5: Программы

        private void RefreshBlacklist()
        {
            List<string> items = _appExclusionService.GetBlacklistSnapshot();
            BlacklistListBox.ItemsSource = items;
            ProgramsCountText.Text = _localizationService.Format("LabelCountFormat", items.Count);
        }

        private void ExclusionsSubOptionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi)
            {
                return;
            }

            _settings.AutoPauseInFullscreen = AutoPauseFullscreenCheckBox.IsChecked == true;
            _settings.BlockManualTriggerInExcluded = BlockManualExcludedCheckBox.IsChecked == true;
            _settingsManager.Save(_settings);
        }

        private void AddProgramFromTextBox()
        {
            if (_appExclusionService.AddExecutable(ProgramExeTextBox.Text))
            {
                ProgramExeTextBox.Clear();
                RefreshBlacklist();
            }
        }

        private void AddProgramButton_Click(object sender, RoutedEventArgs e)
        {
            AddProgramFromTextBox();
        }

        private void ProgramExeTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddProgramFromTextBox();
            }
        }

        private void BrowseExeButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable (*.exe)|*.exe|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                if (_appExclusionService.AddExecutable(dialog.FileName))
                {
                    RefreshBlacklist();
                }
            }
        }

        private void PickRunningAppButton_Click(object sender, RoutedEventArgs e)
        {
            List<RunningWindowAppInfo> apps = _appExclusionService.GetRunningWindowProcesses();
            var menu = new ContextMenu();

            if (apps.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = _localizationService.GetString("ProgramsRunningEmpty"),
                    IsEnabled = false
                });
            }
            else
            {
                foreach (RunningWindowAppInfo app in apps)
                {
                    var item = new MenuItem { Header = app.DisplayLabel, Tag = app.ExecutableName };
                    item.Click += (s, _) =>
                    {
                        if (s is MenuItem mi && mi.Tag is string exeName)
                        {
                            _appExclusionService.AddExecutable(exeName);
                            RefreshBlacklist();
                        }
                    };
                    menu.Items.Add(item);
                }
            }

            menu.PlacementTarget = PickRunningAppButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        private void RemoveProgramButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string exeName)
            {
                _appExclusionService.RemoveExecutable(exeName);
                RefreshBlacklist();
            }
        }

        private void BlacklistListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && BlacklistListBox.SelectedItem is string exeName)
            {
                _appExclusionService.RemoveExecutable(exeName);
                RefreshBlacklist();
            }
        }

        private void ClearProgramsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appExclusionService.Count == 0)
            {
                return;
            }

            if (MessageBox.Show(
                this,
                _localizationService.GetString("ConfirmClearPrograms"),
                _localizationService.GetString("DialogConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                _appExclusionService.ClearBlacklist();
                RefreshBlacklist();
            }
        }

        #endregion

        private void PauseToggleButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePauseState();
        }

        private void HideToTrayButton_Click(object sender, RoutedEventArgs e)
        {
            HideToTray();
        }

        private void ApplyWindows11RoundedCorners()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int preference = DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                }
            }
            catch
            {
                // Игнорируем на версиях ОС до Windows 11
            }
        }

        private static string TruncateTrayText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= 120 ? text : text.Substring(0, 117) + "...";
        }

        private static Drawing.Icon CreateTrayIcon(bool isPaused)
        {
            using var bitmap = new Drawing.Bitmap(32, 32);
            using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Drawing.Color.Transparent);

                Drawing.Color bgColor = isPaused
                    ? Drawing.Color.FromArgb(107, 114, 128)
                    : Drawing.Color.FromArgb(0, 103, 192);

                using var path = CreateRoundedRectanglePath(new Drawing.RectangleF(1, 1, 30, 30), 7);
                using var bgBrush = new Drawing.SolidBrush(bgColor);
                g.FillPath(bgBrush, path);

                if (isPaused)
                {
                    using var barBrush = new Drawing.SolidBrush(Drawing.Color.White);
                    g.FillRectangle(barBrush, 10, 9, 4, 14);
                    g.FillRectangle(barBrush, 18, 9, 4, 14);
                }
                else
                {
                    using var font = new Drawing.Font("Segoe UI", 11.5f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
                    using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);
                    using var format = new Drawing.StringFormat
                    {
                        Alignment = Drawing.StringAlignment.Center,
                        LineAlignment = Drawing.StringAlignment.Center
                    };
                    g.DrawString("А⇄A", font, textBrush, new Drawing.RectangleF(0, 1, 32, 30), format);
                }
            }

            IntPtr hIcon = bitmap.GetHicon();
            try
            {
                using Drawing.Icon temp = Drawing.Icon.FromHandle(hIcon);
                return (Drawing.Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        private static Drawing.Drawing2D.GraphicsPath CreateRoundedRectanglePath(Drawing.RectangleF rect, float radius)
        {
            float d = radius * 2f;
            var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        #region Win32 P/Invoke

        /// <summary>
        /// Устанавливает визуальные атрибуты окна Desktop Window Manager (скругление углов в стиле Windows 11).
        /// </summary>
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>
        /// Освобождает неуправляемый GDI-дескриптор иконки, созданный методом Bitmap.GetHicon().
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Переводит окно настроек на передний план и активирует его фокус.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// Изменяет состояние отображения окна (восстановление из свёрнутого состояния).
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        #endregion
    }
}
