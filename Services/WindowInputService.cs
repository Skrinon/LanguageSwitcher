using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Предоставляет обёртки Win32 API (user32.dll) для имитации ввода (SendInput),
    /// определения активного окна, переключения раскладки клавиатуры и реализации полного конвейера
    /// обработки текста: Шаг 0 (исключения), Приоритет 1 (автозамена), Приоритет 2 (автопереключение),
    /// Приоритет 3 (опечатки) и умный откат по двойному Left Alt.
    /// </summary>
    public sealed class WindowInputService
    {
        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint INPUT_HARDWARE = 2;

        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LEFT = 0x25;
        private const ushort VK_RIGHT = 0x27;
        private const ushort VK_C = 0x43;
        private const ushort VK_V = 0x56;
        private const ushort VK_LSHIFT = 0xA0;
        private const ushort VK_RSHIFT = 0xA1;
        private const ushort VK_LCONTROL = 0xA2;
        private const ushort VK_RCONTROL = 0xA3;
        private const ushort VK_LMENU = 0xA4;
        private const ushort VK_RMENU = 0xA5;
        private const ushort VK_MENU_MASK_DUMMY = 0xE8;

        private const uint WM_CANCELMODE = 0x001F;
        private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

        private const uint GUI_INMENUMODE = 0x00000004;
        private const uint GUI_INMOVESIZE = 0x00000002;

        private const uint KLF_ACTIVATE = 0x00000001;
        private const uint KLF_SETFORPROCESS = 0x00000100;

        private const int ClipboardCopyDelayMs = 40;
        private const int ClipboardExtraWaitMs = 25;
        private const int ClipboardPasteDelayMs = 50;

        private readonly LayoutConverter _layoutConverter;
        private readonly ClipboardManager _clipboardManager;
        private readonly KeyboardHookService _hookService;
        private readonly DictionaryService _dictionaryService;
        private readonly AutoReplaceService _autoReplaceService;
        private readonly AppExclusionService _appExclusionService;
        private readonly UsageStatisticsService _statisticsService;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);

        private int _lastReplacedCaretSteps;
        private AutoActionRecord? _lastAutoAction;
        private AppSettings _settingsSnapshot;

        public event EventHandler<ExclusionCheckResult>? ExclusionStateChanged;
        public event EventHandler<AutoActionRecord>? AutoActionPerformed;
        public event EventHandler<AutoActionRecord>? SmartUndoPerformed;
        public event EventHandler? IgnoredWordsModified;

        public WindowInputService(
            LayoutConverter layoutConverter,
            ClipboardManager clipboardManager,
            KeyboardHookService hookService,
            DictionaryService dictionaryService,
            AutoReplaceService autoReplaceService,
            AppExclusionService appExclusionService,
            UsageStatisticsService statisticsService,
            AppSettings initialSettings)
        {
            _layoutConverter = layoutConverter ?? throw new ArgumentNullException(nameof(layoutConverter));
            _clipboardManager = clipboardManager ?? throw new ArgumentNullException(nameof(clipboardManager));
            _hookService = hookService ?? throw new ArgumentNullException(nameof(hookService));
            _dictionaryService = dictionaryService ?? throw new ArgumentNullException(nameof(dictionaryService));
            _autoReplaceService = autoReplaceService ?? throw new ArgumentNullException(nameof(autoReplaceService));
            _appExclusionService = appExclusionService ?? throw new ArgumentNullException(nameof(appExclusionService));
            _statisticsService = statisticsService ?? throw new ArgumentNullException(nameof(statisticsService));
            _settingsSnapshot = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));

            _hookService.ShouldTreatPunctuationAsDelimiter = ShouldTreatPunctuationAsDelimiter;
        }

        public void UpdateSettingsReference(AppSettings settings)
        {
            _settingsSnapshot = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Сбрасывает состояние повторного циклического переключения и последнего автодействия для отката.
        /// </summary>
        public void ResetCycleState()
        {
            _lastReplacedCaretSteps = 0;
            _lastAutoAction = null;
            _layoutConverter.ResetCycle();
        }

        /// <summary>
        /// Подавляет переход фокуса в верхнее меню активного окна при двойном нажатии Left Alt.
        /// </summary>
        public static void SuppressAltMenuActivation()
        {
            INPUT[] inputs =
            {
                CreateKeyInput(VK_MENU_MASK_DUMMY, keyUp: false),
                CreateKeyInput(VK_MENU_MASK_DUMMY, keyUp: true),
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = VK_LMENU,
                            wScan = 0x38,
                            dwFlags = KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// Выполняет конвейер автоматической обработки при вводе разделителя слова:
        /// Шаг 0 (Исключения) -> Приоритет 1 (Автозамена) -> Приоритет 2 (Автопереключение) -> Приоритет 3 (Опечатки).
        /// </summary>
        public async Task ProcessWordDelimiterAsync(WordDelimiterEventArgs args, AppSettings settings)
        {
            if (args == null || settings == null || string.IsNullOrEmpty(args.TypedWord))
            {
                return;
            }

            if (!await _operationLock.WaitAsync(0))
            {
                return;
            }

            ClipboardSnapshot? savedClipboard = null;

            try
            {
                // Шаг 0: проверка чёрного списка программ и полноэкранного режима (строго при включённой галочке)
                ExclusionCheckResult exclusion = _appExclusionService.EvaluateForegroundWindow(settings);
                ExclusionStateChanged?.Invoke(this, exclusion);
                if (exclusion.IsAutomaticPaused)
                {
                    return;
                }

                AutoActionType actionType = AutoActionType.None;
                string newWord = string.Empty;
                string? activeWinKlid = KeyboardHookService.GetWindowKeyboardLanguage(args.ForegroundHwnd, settings)
                    ?? args.ActiveWindowLanguage;
                string origLang = activeWinKlid ?? _layoutConverter.DetectSourceLanguage(args.TypedWord, settings);
                string targetLang = origLang;

                // Приоритет 1: Пользовательские автозамены и сокращения (autoreplace.json)
                if (_autoReplaceService.TryResolveReplacement(args.TypedWord, settings, out string autoReplacement))
                {
                    actionType = AutoActionType.CustomAutoReplace;
                    newWord = autoReplacement;
                }
                else
                {
                    // Приоритет 2: Автоматическое переключение раскладки (AutoSwitchLayout)
                    AutoSwitchWordResult switchResult = _dictionaryService.EvaluateAutoSwitch(
                        args.TypedWord,
                        activeWinKlid,
                        settings,
                        _layoutConverter);

                    if (switchResult.ShouldSwitch)
                    {
                        actionType = AutoActionType.AutoLayoutSwitch;
                        newWord = switchResult.ConvertedWord;
                        origLang = switchResult.SourceLanguage;
                        targetLang = switchResult.TargetLanguage;
                    }
                    else if (_dictionaryService.TryFixDoubleCapitals(args.TypedWord, settings, out string fixedDoubleCaps))
                    {
                        // Исправление случайной второй заглавной буквы (ПРивет -> Привет)
                        actionType = AutoActionType.DoubleCapitalCorrection;
                        newWord = fixedDoubleCaps;
                    }
                    else
                    {
                        // Приоритет 3: Взвешенное исправление механических опечаток при наборе (строго не Т9)
                        TypoCorrectionResult typoResult = _dictionaryService.EvaluateTypoCorrection(
                            args.TypedWord,
                            origLang,
                            settings,
                            isBurstTyping: args.IsBurstTyping);

                        if (typoResult.ShouldCorrect)
                        {
                            actionType = AutoActionType.TypoCorrection;
                            newWord = typoResult.CorrectedWord;
                        }
                    }
                }

                if (actionType == AutoActionType.None || string.Equals(newWord, args.TypedWord, StringComparison.Ordinal))
                {
                    return;
                }

                // Небольшая задержка, чтобы целевое окно успело обработать только что нажатый разделитель
                await Task.Delay(14);

                if (GetForegroundWindow() != args.ForegroundHwnd)
                {
                    return;
                }

                int selectSteps = CountCaretSteps(args.TypedWord) + 1;
                savedClipboard = _clipboardManager.SaveClipboard();

                SelectCharactersLeft(selectSteps);
                await Task.Delay(15);

                _clipboardManager.ClearClipboard();
                uint seqBeforeCopy = GetClipboardSequenceNumber();
                SendCtrlShortcut(VK_C);

                await Task.Delay(ClipboardCopyDelayMs);
                if (GetClipboardSequenceNumber() == seqBeforeCopy)
                {
                    await Task.Delay(ClipboardExtraWaitMs);
                }

                string captured = _clipboardManager.GetText();
                if (string.IsNullOrEmpty(captured))
                {
                    return;
                }

                string capturedWordPart = captured.Length >= args.TypedWord.Length
                    ? captured.Substring(0, args.TypedWord.Length)
                    : string.Empty;

                if (!string.Equals(capturedWordPart, args.TypedWord, StringComparison.OrdinalIgnoreCase))
                {
                    // Если текст в активном окне отличается (например, Enter отправил сообщение в мессенджере), отменяем выделение
                    SendSingleKey(VK_RIGHT, extended: true);
                    return;
                }

                string actualDelimiter = captured.Length > args.TypedWord.Length
                    ? captured.Substring(args.TypedWord.Length)
                    : (args.IsEnterDelimiter ? "\r\n" : args.DelimiterChar.ToString());

                // Для многострочной вставки используем стандартные переносы \r\n Windows
                string clipboardWord = newWord.Replace("\r\n", "\n").Replace("\n", "\r\n");
                string finalPayload = clipboardWord + actualDelimiter;

                if (!_clipboardManager.SetText(finalPayload))
                {
                    return;
                }

                await Task.Delay(12);
                SendCtrlShortcut(VK_V);
                await Task.Delay(ClipboardPasteDelayMs);

                if (actionType == AutoActionType.AutoLayoutSwitch)
                {
                    SwitchActiveWindowLayout(targetLang);
                }

                double timeSaved = actionType switch
                {
                    AutoActionType.CustomAutoReplace => _statisticsService.RecordAutoReplace(args.TypedWord.Length, newWord.Length),
                    AutoActionType.AutoLayoutSwitch => _statisticsService.RecordLayoutSwitch(args.TypedWord.Length),
                    AutoActionType.TypoCorrection => _statisticsService.RecordTypoFix(args.TypedWord.Length),
                    AutoActionType.DoubleCapitalCorrection => _statisticsService.RecordDoubleCapitalFix(),
                    _ => 0.0
                };

                var record = new AutoActionRecord
                {
                    ActionType = actionType,
                    OriginalWord = args.TypedWord,
                    ReplacedText = clipboardWord,
                    DelimiterText = actualDelimiter,
                    OriginalLanguage = origLang,
                    TargetLanguage = targetLang,
                    TargetWindowHwnd = args.ForegroundHwnd,
                    TimeSavedSeconds = timeSaved
                };

                _lastAutoAction = record;
                _lastReplacedCaretSteps = CountCaretSteps(finalPayload);
                _hookService.SetTrackedWordLength(
                    CountCaretSteps(clipboardWord),
                    CountCaretSteps(actualDelimiter),
                    clipboardWord);

                AutoActionPerformed?.Invoke(this, record);
            }
            finally
            {
                if (savedClipboard != null)
                {
                    await Task.Delay(15);
                    _clipboardManager.RestoreClipboard(savedClipboard);
                }

                _operationLock.Release();
            }
        }

        /// <summary>
        /// Выполняет обработку двойного нажатия Left Alt:
        /// Шаг 0 (блокировка в исключённых окнах) -> Умный откат автодействия -> Ручная конвертация текста.
        /// </summary>
        public async Task<ConversionResult?> ProcessTriggerAsync(AppSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            if (!await _operationLock.WaitAsync(0))
            {
                return null;
            }

            ClipboardSnapshot? savedClipboard = null;

            try
            {
                // Шаг 0: при включённой галочке EnableAppExclusions и BlockManualTriggerInExcluded блокируем ручной триггер
                ExclusionCheckResult exclusion = _appExclusionService.EvaluateForegroundWindow(settings);
                ExclusionStateChanged?.Invoke(this, exclusion);
                if (exclusion.IsManualBlocked)
                {
                    return null;
                }

                await Task.Delay(12);
                EnsureModifiersReleased();
                CancelActiveMenuModeIfNeeded();

                // Умный откат по двойному Left Alt сразу после срабатывания автозамены/автопереключения/опечатки
                IntPtr currentForeground = GetForegroundWindow();
                if (_lastAutoAction != null && _lastAutoAction.TargetWindowHwnd == currentForeground)
                {
                    AutoActionRecord undoAction = _lastAutoAction;
                    _lastAutoAction = null;

                    savedClipboard = _clipboardManager.SaveClipboard();
                    int stepsToRevert = CountCaretSteps(undoAction.ReplacedText + undoAction.DelimiterText);
                    if (stepsToRevert > 0)
                    {
                        SelectCharactersLeft(stepsToRevert);
                        await Task.Delay(15);

                        string revertPayload = undoAction.OriginalWord + undoAction.DelimiterText;
                        if (_clipboardManager.SetText(revertPayload))
                        {
                            await Task.Delay(12);
                            SendCtrlShortcut(VK_V);
                            await Task.Delay(ClipboardPasteDelayMs);
                        }
                    }

                    if (undoAction.ActionType == AutoActionType.AutoLayoutSwitch)
                    {
                        SwitchActiveWindowLayout(undoAction.OriginalLanguage);
                        if (_dictionaryService.AddIgnoredWords(new[] { undoAction.OriginalWord }) > 0)
                        {
                            IgnoredWordsModified?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    else if (undoAction.ActionType == AutoActionType.TypoCorrection
                        || undoAction.ActionType == AutoActionType.DoubleCapitalCorrection)
                    {
                        if (_dictionaryService.AddIgnoredWords(new[] { undoAction.OriginalWord }) > 0)
                        {
                            IgnoredWordsModified?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    // Для CustomAutoReplace (Приоритет 1) слово в ignored_words.json НЕ добавляем

                    _statisticsService.RevertAutoAction(undoAction);
                    _hookService.ResetTrackedWord();
                    SmartUndoPerformed?.Invoke(this, undoAction);
                    return null;
                }

                bool isContinuation = _layoutConverter.IsCycleActive;

                // 1. Сохраняем текущее содержимое буфера обмена пользователя
                savedClipboard = _clipboardManager.SaveClipboard();

                // 2. Сценарий 1: проверяем, есть ли уже выделенный текст в активном окне
                _clipboardManager.ClearClipboard();
                uint seqBeforeCopy = GetClipboardSequenceNumber();
                SendCtrlShortcut(VK_C);

                await Task.Delay(ClipboardCopyDelayMs);
                if (GetClipboardSequenceNumber() == seqBeforeCopy)
                {
                    await Task.Delay(ClipboardExtraWaitMs);
                }

                string capturedText = _clipboardManager.GetText();
                bool usedWordAutoSelection = false;

                // 3. Сценарий 2: если текст не был выделен, выделяем последнее слово слева от курсора
                if (string.IsNullOrEmpty(capturedText))
                {
                    usedWordAutoSelection = true;

                    if (isContinuation && _lastReplacedCaretSteps > 0)
                    {
                        SelectCharactersLeft(_lastReplacedCaretSteps);
                    }
                    else
                    {
                        (int wordLength, int trailingSpaces) = _hookService.GetTrackedWordSnapshot();
                        int trackedTotal = wordLength + trailingSpaces;

                        if (trackedTotal > 0)
                        {
                            SelectCharactersLeft(trackedTotal);
                        }
                        else
                        {
                            SendCtrlShiftLeft();
                        }
                    }

                    await Task.Delay(20);
                    _clipboardManager.ClearClipboard();
                    uint seqBeforeWordCopy = GetClipboardSequenceNumber();
                    SendCtrlShortcut(VK_C);

                    await Task.Delay(ClipboardCopyDelayMs);
                    if (GetClipboardSequenceNumber() == seqBeforeWordCopy)
                    {
                        await Task.Delay(ClipboardExtraWaitMs);
                    }

                    capturedText = _clipboardManager.GetText();
                }

                if (string.IsNullOrWhiteSpace(capturedText))
                {
                    return null;
                }

                SplitLeadingAndTrailingWhitespace(
                    capturedText,
                    out string leadingWhitespace,
                    out string coreText,
                    out string trailingWhitespace);

                if (string.IsNullOrEmpty(coreText))
                {
                    return null;
                }

                // 4. Конвертируем текст по карте раскладок с сохранением регистра и учётом цикла языков
                string? activeWindowLang = KeyboardHookService.GetWindowKeyboardLanguage(currentForeground, settings);
                ConversionResult conversion = _layoutConverter.ConvertWithCycle(
                    coreText,
                    settings,
                    isContinuation: isContinuation,
                    activeWindowLanguage: activeWindowLang);

                if (!conversion.Success)
                {
                    return conversion;
                }

                string finalOutput = string.Concat(leadingWhitespace, conversion.ConvertedText, trailingWhitespace);

                // 5. Помещаем результат в буфер обмена и вставляем через Ctrl + V
                if (!_clipboardManager.SetText(finalOutput))
                {
                    return conversion;
                }

                await Task.Delay(15);
                SendCtrlShortcut(VK_V);
                await Task.Delay(ClipboardPasteDelayMs);

                _lastReplacedCaretSteps = CountCaretSteps(finalOutput);

                if (usedWordAutoSelection || _lastReplacedCaretSteps > 0)
                {
                    _hookService.SetTrackedWordLength(
                        CountCaretSteps(string.Concat(leadingWhitespace, conversion.ConvertedText)),
                        CountCaretSteps(trailingWhitespace),
                        conversion.ConvertedText);
                }

                // 6. Переключаем системную раскладку клавиатуры в активном окне на целевой язык
                SwitchActiveWindowLayout(conversion.TargetLanguage);

                if (!conversion.IsCycleContinuation)
                {
                    _statisticsService.RecordLayoutSwitch(coreText.Length);
                }

                return conversion;
            }
            finally
            {
                if (savedClipboard != null)
                {
                    await Task.Delay(15);
                    _clipboardManager.RestoreClipboard(savedClipboard);
                }

                _operationLock.Release();
            }
        }

        /// <summary>
        /// Переключает системную раскладку клавиатуры в активном окне на указанный KLID/язык.
        /// </summary>
        public static void SwitchActiveWindowLayout(string targetKlid)
        {
            KeyboardLayoutInfo info = UniversalLayoutService.Instance.GetLayoutInfo(targetKlid);
            IntPtr targetHkl = ResolveKeyboardLayoutHandle(info);
            if (targetHkl == IntPtr.Zero)
            {
                return;
            }

            IntPtr foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == IntPtr.Zero)
            {
                return;
            }

            uint threadId = GetWindowThreadProcessId(foregroundHwnd, out _);
            var guiInfo = new GUITHREADINFO
            {
                cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>()
            };

            IntPtr focusHwnd = IntPtr.Zero;
            if (threadId != 0 && GetGUIThreadInfo(threadId, ref guiInfo))
            {
                focusHwnd = guiInfo.hwndFocus;
            }

            ActivateKeyboardLayout(targetHkl, KLF_SETFORPROCESS);

            if (focusHwnd != IntPtr.Zero && focusHwnd != foregroundHwnd)
            {
                PostMessage(focusHwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, targetHkl);
            }

            PostMessage(foregroundHwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, targetHkl);
        }

        private bool ShouldTreatPunctuationAsDelimiter(string currentWord, char punctuationChar, string? windowLang)
        {
            AppSettings settings = _settingsSnapshot;
            if (_autoReplaceService.TryResolveReplacement(currentWord, settings, out _))
            {
                return true;
            }

            if (_dictionaryService.TryFixDoubleCapitals(currentWord, settings, out _))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(windowLang) && _dictionaryService.ContainsWord(windowLang, currentWord))
            {
                return true;
            }

            return _dictionaryService.EvaluateAutoSwitch(currentWord, windowLang, settings, _layoutConverter).ShouldSwitch;
        }

        private static IntPtr ResolveKeyboardLayoutHandle(KeyboardLayoutInfo info)
        {
            IntPtr fallbackByLangId = IntPtr.Zero;
            int count = GetKeyboardLayoutList(0, null);
            if (count > 0)
            {
                var installed = new IntPtr[count];
                int actual = GetKeyboardLayoutList(count, installed);
                for (int i = 0; i < actual; i++)
                {
                    string? installedKlid = UniversalLayoutService.Instance.ResolveKlidFromHkl(installed[i]);
                    if (string.Equals(installedKlid, info.Klid, StringComparison.OrdinalIgnoreCase))
                    {
                        return installed[i];
                    }

                    ushort langId = (ushort)(installed[i].ToInt64() & 0xFFFF);
                    if (langId == info.LanguageId && fallbackByLangId == IntPtr.Zero)
                    {
                        fallbackByLangId = installed[i];
                    }
                }
            }

            IntPtr loaded = LoadKeyboardLayout(info.Klid, KLF_ACTIVATE);
            return loaded != IntPtr.Zero ? loaded : fallbackByLangId;
        }

        private static void CancelActiveMenuModeIfNeeded()
        {
            IntPtr foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == IntPtr.Zero)
            {
                return;
            }

            uint threadId = GetWindowThreadProcessId(foregroundHwnd, out _);
            if (threadId == 0)
            {
                return;
            }

            var guiInfo = new GUITHREADINFO
            {
                cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>()
            };

            if (GetGUIThreadInfo(threadId, ref guiInfo)
                && (guiInfo.flags & (GUI_INMENUMODE | GUI_INMOVESIZE)) != 0)
            {
                if (guiInfo.hwndMenuOwner != IntPtr.Zero)
                {
                    PostMessage(guiInfo.hwndMenuOwner, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                }

                if (guiInfo.hwndActive != IntPtr.Zero)
                {
                    PostMessage(guiInfo.hwndActive, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                }

                PostMessage(foregroundHwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
            }
        }

        private static void EnsureModifiersReleased()
        {
            ushort[] modifiers =
            {
                VK_LMENU, VK_RMENU, VK_MENU,
                VK_LCONTROL, VK_RCONTROL, VK_CONTROL,
                VK_LSHIFT, VK_RSHIFT, VK_SHIFT
            };

            foreach (ushort vk in modifiers)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    INPUT[] release = { CreateKeyInput(vk, keyUp: true) };
                    SendInput(1, release, Marshal.SizeOf<INPUT>());
                }
            }
        }

        private static void SendCtrlShortcut(ushort keyVk)
        {
            INPUT[] inputs =
            {
                CreateKeyInput(VK_CONTROL, keyUp: false),
                CreateKeyInput(keyVk, keyUp: false),
                CreateKeyInput(keyVk, keyUp: true),
                CreateKeyInput(VK_CONTROL, keyUp: true)
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendSingleKey(ushort keyVk, bool extended = false)
        {
            INPUT[] inputs =
            {
                CreateKeyInput(keyVk, keyUp: false, extended: extended),
                CreateKeyInput(keyVk, keyUp: true, extended: extended)
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendCtrlShiftLeft()
        {
            INPUT[] inputs =
            {
                CreateKeyInput(VK_CONTROL, keyUp: false),
                CreateKeyInput(VK_SHIFT, keyUp: false),
                CreateKeyInput(VK_LEFT, keyUp: false, extended: true),
                CreateKeyInput(VK_LEFT, keyUp: true, extended: true),
                CreateKeyInput(VK_SHIFT, keyUp: true),
                CreateKeyInput(VK_CONTROL, keyUp: true)
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static void SelectCharactersLeft(int charCount)
        {
            if (charCount <= 0)
            {
                return;
            }

            int clampedCount = Math.Min(charCount, 256);
            var inputs = new INPUT[2 + (clampedCount * 2)];
            int idx = 0;

            inputs[idx++] = CreateKeyInput(VK_SHIFT, keyUp: false);
            for (int i = 0; i < clampedCount; i++)
            {
                inputs[idx++] = CreateKeyInput(VK_LEFT, keyUp: false, extended: true);
                inputs[idx++] = CreateKeyInput(VK_LEFT, keyUp: true, extended: true);
            }
            inputs[idx] = CreateKeyInput(VK_SHIFT, keyUp: true);

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static INPUT CreateKeyInput(ushort virtualKey, bool keyUp, bool extended = false)
        {
            uint flags = 0;
            if (keyUp)
            {
                flags |= KEYEVENTF_KEYUP;
            }

            if (extended)
            {
                flags |= KEYEVENTF_EXTENDEDKEY;
            }

            return new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private static void SplitLeadingAndTrailingWhitespace(
            string input,
            out string leading,
            out string core,
            out string trailing)
        {
            int start = 0;
            while (start < input.Length && char.IsWhiteSpace(input[start]))
            {
                start++;
            }

            int end = input.Length - 1;
            while (end >= start && char.IsWhiteSpace(input[end]))
            {
                end--;
            }

            if (start > end)
            {
                leading = input;
                core = string.Empty;
                trailing = string.Empty;
                return;
            }

            leading = start > 0 ? input.Substring(0, start) : string.Empty;
            core = input.Substring(start, end - start + 1);
            trailing = end < input.Length - 1 ? input.Substring(end + 1) : string.Empty;
        }

        private static int CountCaretSteps(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int steps = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                steps++;
            }

            return steps;
        }

        #region Win32 P/Invoke

        /// <summary>
        /// Структура ввода Win32 для функции SendInput.
        /// Размер объединения InputUnion включает MOUSEINPUT, KEYBDINPUT и HARDWAREINPUT
        /// для корректного выравнивания на 32-битных (28 байт) и 64-битных (40 байт) системах.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        /// <summary>
        /// Объединение (union) данных события ввода для мыши, клавиатуры и аппаратных устройств.
        /// </summary>
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;

            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        /// <summary>
        /// Данные события клавиатуры для SendInput.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// Данные события мыши для SendInput (участвует в вычислении размера union).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        /// <summary>
        /// Данные аппаратного события для SendInput (участвует в вычислении размера union).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        /// <summary>
        /// Информация о GUI-потоке активного окна (фокус ввода, владелец меню, флаги состояния меню).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        /// <summary>
        /// Синтезирует нажатия клавиш и клики, помещая их в системный поток ввода Windows.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        /// <summary>
        /// Возвращает дескриптор (HWND) текущего активного окна переднего плана.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Получает идентификатор потока и процесса (PID), которым принадлежит указанное окно.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// Возвращает состояние GUI-потока: окно в фокусе ввода (hwndFocus) и флаги активности системного меню.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        /// <summary>
        /// Помещает оконное сообщение в очередь указанного окна без ожидания обработки (WM_INPUTLANGCHANGEREQUEST, WM_CANCELMODE).
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Загружает идентификатор раскладки клавиатуры (HKL) по строковому коду KLID (например, "00000419").
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

        /// <summary>
        /// Активирует указанную раскладку клавиатуры (HKL) для текущего потока или всего процесса.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint Flags);

        /// <summary>
        /// Возвращает массив дескрипторов HKL всех раскладок клавиатуры, установленных в текущей сессии Windows.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

        /// <summary>
        /// Возвращает монотонно возрастающий номер версии содержимого буфера обмена для отслеживания завершения Ctrl+C.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        /// <summary>
        /// Проверяет текущее состояние виртуальной клавиши на момент вызова.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        #endregion
    }
}
