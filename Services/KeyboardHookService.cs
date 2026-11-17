using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Данные о введённом слове, скорости набора и разделителе для конвейера автоматической обработки.
    /// </summary>
    public sealed class WordDelimiterEventArgs : EventArgs
    {
        public string TypedWord { get; }
        public char DelimiterChar { get; }
        public bool IsEnterDelimiter { get; }
        public string? ActiveWindowLanguage { get; }
        public IntPtr ForegroundHwnd { get; }
        public bool IsBurstTyping { get; }

        public WordDelimiterEventArgs(
            string typedWord,
            char delimiterChar,
            bool isEnterDelimiter,
            string? activeWindowLanguage,
            IntPtr foregroundHwnd,
            bool isBurstTyping = false)
        {
            TypedWord = typedWord;
            DelimiterChar = delimiterChar;
            IsEnterDelimiter = isEnterDelimiter;
            ActiveWindowLanguage = activeWindowLanguage;
            ForegroundHwnd = foregroundHwnd;
            IsBurstTyping = isBurstTyping;
        }
    }

    /// <summary>
    /// Устанавливает глобальные низкоуровневые хуки клавиатуры (WH_KEYBOARD_LL) и мыши (WH_MOUSE_LL)
    /// для распознавания двойного быстрого нажатия Left Alt, защиты от ложных срабатываний,
    /// отслеживания буфера слова и перехвата разделителей слова.
    /// </summary>
    public sealed class KeyboardHookService : IDisposable
    {
        // Константы типов хуков Win32
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL = 14;

        // Сообщения клавиатуры
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        // Сообщения мыши
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_XBUTTONDOWN = 0x020B;

        // Флаг инжектированного события клавиатуры (SendInput)
        private const uint LLKHF_INJECTED = 0x00000010;

        // Виртуальные коды клавиш
        private const uint VK_BACK = 0x08;
        private const uint VK_TAB = 0x09;
        private const uint VK_RETURN = 0x0D;
        private const uint VK_SHIFT = 0x10;
        private const uint VK_CONTROL = 0x11;
        private const uint VK_MENU = 0x12;
        private const uint VK_PAUSE = 0x13;
        private const uint VK_CAPITAL = 0x14;
        private const uint VK_ESCAPE = 0x1B;
        private const uint VK_SPACE = 0x20;
        private const uint VK_PRIOR = 0x21;
        private const uint VK_NEXT = 0x22;
        private const uint VK_END = 0x23;
        private const uint VK_HOME = 0x24;
        private const uint VK_LEFT = 0x25;
        private const uint VK_UP = 0x26;
        private const uint VK_RIGHT = 0x27;
        private const uint VK_DOWN = 0x28;
        private const uint VK_INSERT = 0x2D;
        private const uint VK_DELETE = 0x2E;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;
        private const uint VK_APPS = 0x5D;
        private const uint VK_LSHIFT = 0xA0;
        private const uint VK_RSHIFT = 0xA1;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_RCONTROL = 0xA3;
        private const uint VK_LMENU = 0xA4;  // Left Alt
        private const uint VK_RMENU = 0xA5;  // Right Alt

        // OEM-клавиши пунктуации (соответствуют символам раскладок EN/RU/UK)
        private const uint VK_OEM_1 = 0xBA;
        private const uint VK_OEM_PLUS = 0xBB;
        private const uint VK_OEM_COMMA = 0xBC;
        private const uint VK_OEM_MINUS = 0xBD;
        private const uint VK_OEM_PERIOD = 0xBE;
        private const uint VK_OEM_2 = 0xBF;
        private const uint VK_OEM_3 = 0xC0;
        private const uint VK_OEM_4 = 0xDB;
        private const uint VK_OEM_5 = 0xDC;
        private const uint VK_OEM_6 = 0xDD;
        private const uint VK_OEM_7 = 0xDE;
        private const uint VK_OEM_8 = 0xDF;
        private const uint VK_OEM_102 = 0xE2;

        // Пороговые тайминги двойного нажатия Left Alt
        private const long MaxTapHoldMs = 300;
        private const long MaxDoubleTapIntervalMs = 350;
        private const int MaxTrackedWordLength = 128;

        // Флаг ToUnicodeEx (Windows 10+): не изменять состояние мёртвых клавиш ядра
        private const uint TOUNICODE_DO_NOT_CHANGE_STATE = 0x00000004;

        private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        private readonly Dispatcher _dispatcher;
        private readonly LowLevelHookProc _keyboardProc;
        private readonly LowLevelHookProc _mouseProc;

        private IntPtr _keyboardHookHandle = IntPtr.Zero;
        private IntPtr _mouseHookHandle = IntPtr.Zero;
        private bool _disposed;

        // Конфигурация и состояние пользовательского триггера ручного переключения
        private uint _triggerVirtualKey = HotkeyFormatter.DefaultTriggerVirtualKey;
        private uint _triggerModifiers;
        private bool _triggerRequireDoubleTap = true;
        private bool _isTriggerKeyDown;
        private bool _otherKeyPressedWhileTriggerDown;
        private bool _suppressNextTriggerKeyUp;
        private long _triggerKeyDownTick;
        private long _firstTapUpTick;
        private int _tapCount;
        private readonly HashSet<uint> _simulatedDownKeys = new HashSet<uint>();

        // Внутренний буфер отслеживания текущего слова, его символов и телеметрии скорости печати
        private readonly object _bufferLock = new object();
        private readonly StringBuilder _typedWordBuffer = new StringBuilder(64);
        private IntPtr _lastForegroundHwnd = IntPtr.Zero;
        private int _trackedWordLength;
        private int _trailingSpaceCount;
        private long _lastKeyDownTimestamp;
        private double _intervalSumMs;
        private double _minIntervalMs = double.MaxValue;
        private int _intervalCount;

        private const double BurstAvgIntervalThresholdMs = 130.0;
        private const double BurstMinIntervalThresholdMs = 45.0;

        /// <summary>
        /// Приостанавливает обработку триггера и автоматических функций (ручная пауза).
        /// </summary>
        public bool IsPaused { get; set; }

        /// <summary>
        /// Блокирует срабатывание триггера, пока пользователь в окне настроек назначает новую клавишу.
        /// </summary>
        public bool IsRecordingTrigger { get; set; }

        public uint TriggerVirtualKey
        {
            get => _triggerVirtualKey;
            set
            {
                _triggerVirtualKey = value == 0 ? HotkeyFormatter.DefaultTriggerVirtualKey : value;
                ResetTriggerTapState();
            }
        }

        public uint TriggerModifiers
        {
            get => _triggerModifiers;
            set
            {
                _triggerModifiers = value;
                ResetTriggerTapState();
            }
        }

        public bool TriggerRequireDoubleTap
        {
            get => _triggerRequireDoubleTap;
            set
            {
                _triggerRequireDoubleTap = value;
                ResetTriggerTapState();
            }
        }

        /// <summary>
        /// Применяет настройки пользовательской клавиши-триггера без перезапуска глобального хука.
        /// </summary>
        public void ApplyTriggerSettings(AppSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _triggerVirtualKey = settings.TriggerVirtualKey == 0
                ? HotkeyFormatter.DefaultTriggerVirtualKey
                : settings.TriggerVirtualKey;
            _triggerModifiers = settings.TriggerModifiers & ~HotkeyFormatter.GetModifierBit(_triggerVirtualKey);
            _triggerRequireDoubleTap = settings.TriggerRequireDoubleTap;
            ResetTriggerTapState();
        }

        /// <summary>
        /// Предикат, определяющий, считать ли пунктуацию (., ; : ?) разделителем слова в текущей раскладке.
        /// </summary>
        public Func<string, char, string?, bool>? ShouldTreatPunctuationAsDelimiter { get; set; }

        /// <summary>
        /// Событие срабатывания двойного нажатия Left Alt (вызывается в UI-потоке Dispatcher).
        /// </summary>
        public event EventHandler? DoubleLeftAltTriggered;

        /// <summary>
        /// Событие завершения ввода слова разделителем (Space, Enter, ., ,, !, ?, ;, :).
        /// </summary>
        public event EventHandler<WordDelimiterEventArgs>? WordDelimiterTyped;

        /// <summary>
        /// Событие сброса контекста ввода (клик мыши, навигация, новое редактирование, смена окна),
        /// сбрасывающее цикл переключения и буфер отката.
        /// </summary>
        public event EventHandler? InputContextReset;

        public KeyboardHookService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _keyboardProc = KeyboardHookCallback;
            _mouseProc = MouseHookCallback;
        }

        /// <summary>
        /// Возвращает текущее отслеживаемое количество символов последнего набранного слова и пробелов после него.
        /// </summary>
        public (int WordLength, int TrailingSpaces) GetTrackedWordSnapshot()
        {
            lock (_bufferLock)
            {
                IntPtr currentHwnd = GetForegroundWindow();
                if (currentHwnd != _lastForegroundHwnd)
                {
                    _lastForegroundHwnd = currentHwnd;
                    ResetWordBufferLocked();
                }

                return (_trackedWordLength, _trailingSpaceCount);
            }
        }

        /// <summary>
        /// Обновляет длину отслеживаемого слова после программной замены текста.
        /// </summary>
        public void SetTrackedWordLength(int wordLength, int trailingSpaces, string? newWordText = null)
        {
            lock (_bufferLock)
            {
                _lastForegroundHwnd = GetForegroundWindow();
                ResetWordBufferLocked();
                _trackedWordLength = Math.Max(0, wordLength);
                _trailingSpaceCount = Math.Max(0, trailingSpaces);
                if (!string.IsNullOrEmpty(newWordText))
                {
                    _typedWordBuffer.Append(newWordText);
                }
            }
        }

        /// <summary>
        /// Сбрасывает отслеживаемый буфер слова.
        /// </summary>
        public void ResetTrackedWord()
        {
            lock (_bufferLock)
            {
                ResetWordBufferLocked();
            }
        }

        private void ResetWordBufferLocked()
        {
            _trackedWordLength = 0;
            _trailingSpaceCount = 0;
            _typedWordBuffer.Clear();
            _lastKeyDownTimestamp = 0;
            _intervalSumMs = 0;
            _minIntervalMs = double.MaxValue;
            _intervalCount = 0;
        }

        private bool IsBurstTypingLocked()
        {
            if (_intervalCount <= 0)
            {
                return false;
            }

            double avgIntervalMs = _intervalSumMs / _intervalCount;
            return avgIntervalMs < BurstAvgIntervalThresholdMs || _minIntervalMs < BurstMinIntervalThresholdMs;
        }

        /// <summary>
        /// Определяет KLID текущей активной раскладки клавиатуры в указанном окне (учитывая дочернее окно фокуса).
        /// </summary>
        public static string? GetWindowKeyboardLanguage(IntPtr hwnd, AppSettings? settings = null)
        {
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            if (threadId == 0)
            {
                return null;
            }

            var guiInfo = new GUITHREADINFO
            {
                cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>()
            };

            if (GetGUIThreadInfo(threadId, ref guiInfo) && guiInfo.hwndFocus != IntPtr.Zero)
            {
                uint focusThreadId = GetWindowThreadProcessId(guiInfo.hwndFocus, out _);
                if (focusThreadId != 0)
                {
                    threadId = focusThreadId;
                }
            }

            IntPtr hkl = GetKeyboardLayout(threadId);
            if (hkl == IntPtr.Zero)
            {
                return null;
            }

            return UniversalLayoutService.Instance.ResolveKlidFromHkl(hkl, settings);
        }

        /// <summary>
        /// Устанавливает глобальные низкоуровневые хуки клавиатуры и мыши.
        /// </summary>
        public void Start()
        {
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                return;
            }

            using Process currentProcess = Process.GetCurrentProcess();
            using ProcessModule? mainModule = currentProcess.MainModule;
            IntPtr moduleHandle = mainModule != null ? GetModuleHandle(mainModule.ModuleName) : IntPtr.Zero;

            _keyboardHookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
            if (_keyboardHookHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Не удалось установить хук клавиатуры WH_KEYBOARD_LL (ошибка Win32: {Marshal.GetLastWin32Error()}).");
            }

            _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
        }

        /// <summary>
        /// Снимает установленные низкоуровневые хуки.
        /// </summary>
        public void Stop()
        {
            if (_keyboardHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }

            if (_mouseHookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if ((hookStruct.flags & LLKHF_INJECTED) == 0)
                {
                    int msg = wParam.ToInt32();
                    bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (isKeyDown)
                    {
                        if (HandleKeyDownCore(hookStruct.vkCode, hookStruct.scanCode, Environment.TickCount64, isSimulation: false, out _))
                        {
                            return (IntPtr)1;
                        }
                    }
                    else if (isKeyUp)
                    {
                        if (HandleKeyUpCore(hookStruct.vkCode, Environment.TickCount64, isSimulation: false, out _))
                        {
                            return (IntPtr)1;
                        }
                    }
                }
            }

            return CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        /// <summary>
        /// Детерминированно симулирует событие клавиши для автоматического тестирования пользовательских триггеров.
        /// Возвращает true, если событие вызвало срабатывание триггера.
        /// </summary>
        public bool SimulateKeyEventForTest(uint vkCode, bool isKeyDown, long timestampMs)
        {
            if (isKeyDown)
            {
                _simulatedDownKeys.Add(vkCode);
                HandleKeyDownCore(vkCode, 0, timestampMs, isSimulation: true, out bool triggered);
                return triggered;
            }
            else
            {
                _simulatedDownKeys.Remove(vkCode);
                HandleKeyUpCore(vkCode, timestampMs, isSimulation: true, out bool triggered);
                return triggered;
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN
                    || msg == WM_RBUTTONDOWN
                    || msg == WM_MBUTTONDOWN
                    || msg == WM_XBUTTONDOWN)
                {
                    ResetTriggerTapState();
                    ResetTrackedWord();
                    RaiseInputContextReset();
                }
            }

            return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        private bool MatchesTriggerKey(uint vkCode)
        {
            if (vkCode == _triggerVirtualKey)
            {
                return true;
            }

            return _triggerVirtualKey switch
            {
                VK_MENU => vkCode == VK_LMENU || vkCode == VK_RMENU,
                VK_CONTROL => vkCode == VK_LCONTROL || vkCode == VK_RCONTROL,
                VK_SHIFT => vkCode == VK_LSHIFT || vkCode == VK_RSHIFT,
                _ => false
            };
        }

        private bool IsVirtualKeyDown(uint vkCode, bool isSimulation)
        {
            if (isSimulation)
            {
                if (_simulatedDownKeys.Contains(vkCode))
                {
                    return true;
                }

                return vkCode switch
                {
                    VK_CONTROL => _simulatedDownKeys.Contains(VK_LCONTROL) || _simulatedDownKeys.Contains(VK_RCONTROL),
                    VK_MENU => _simulatedDownKeys.Contains(VK_LMENU) || _simulatedDownKeys.Contains(VK_RMENU),
                    VK_SHIFT => _simulatedDownKeys.Contains(VK_LSHIFT) || _simulatedDownKeys.Contains(VK_RSHIFT),
                    _ => false
                };
            }

            return IsKeyDownAsync(vkCode);
        }

        private bool AreTriggerModifiersMatched(bool isSimulation)
        {
            uint ownModBit = HotkeyFormatter.GetModifierBit(_triggerVirtualKey);
            uint requiredMods = _triggerModifiers & ~ownModBit;

            bool ctrlDown = IsVirtualKeyDown(VK_CONTROL, isSimulation)
                || IsVirtualKeyDown(VK_LCONTROL, isSimulation)
                || IsVirtualKeyDown(VK_RCONTROL, isSimulation);
            bool altDown = IsVirtualKeyDown(VK_MENU, isSimulation)
                || IsVirtualKeyDown(VK_LMENU, isSimulation)
                || IsVirtualKeyDown(VK_RMENU, isSimulation);
            bool shiftDown = IsVirtualKeyDown(VK_SHIFT, isSimulation)
                || IsVirtualKeyDown(VK_LSHIFT, isSimulation)
                || IsVirtualKeyDown(VK_RSHIFT, isSimulation);
            bool winDown = IsVirtualKeyDown(VK_LWIN, isSimulation)
                || IsVirtualKeyDown(VK_RWIN, isSimulation);

            if (ownModBit != HotkeyFormatter.ModCtrl && ((requiredMods & HotkeyFormatter.ModCtrl) != 0) != ctrlDown)
            {
                return false;
            }

            if (ownModBit != HotkeyFormatter.ModAlt && ((requiredMods & HotkeyFormatter.ModAlt) != 0) != altDown)
            {
                return false;
            }

            if (ownModBit != HotkeyFormatter.ModShift && ((requiredMods & HotkeyFormatter.ModShift) != 0) != shiftDown)
            {
                return false;
            }

            if (ownModBit != HotkeyFormatter.ModWin && ((requiredMods & HotkeyFormatter.ModWin) != 0) != winDown)
            {
                return false;
            }

            return true;
        }

        private bool IsTriggerUsingAlt()
        {
            return (_triggerModifiers & HotkeyFormatter.ModAlt) != 0
                || HotkeyFormatter.GetModifierBit(_triggerVirtualKey) == HotkeyFormatter.ModAlt;
        }

        private void FireTriggerEvent(bool isSimulation)
        {
            if (isSimulation)
            {
                DoubleLeftAltTriggered?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (IsTriggerUsingAlt())
            {
                WindowInputService.SuppressAltMenuActivation();
            }

            _dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => DoubleLeftAltTriggered?.Invoke(this, EventArgs.Empty)));
        }

        private bool HandleKeyDownCore(uint vkCode, uint scanCode, long now, bool isSimulation, out bool triggered)
        {
            triggered = false;

            if (IsRecordingTrigger)
            {
                ResetTriggerTapState();
                return false;
            }

            if (!IsPaused && MatchesTriggerKey(vkCode) && AreTriggerModifiersMatched(isSimulation))
            {
                bool isImmediateSingleKeyOrCombo =
                    !_triggerRequireDoubleTap
                    && (!HotkeyFormatter.IsModifierVirtualKey(_triggerVirtualKey) || (_triggerModifiers & ~HotkeyFormatter.GetModifierBit(_triggerVirtualKey)) != 0);

                if (isImmediateSingleKeyOrCombo)
                {
                    if (!_isTriggerKeyDown)
                    {
                        _isTriggerKeyDown = true;
                        _suppressNextTriggerKeyUp = true;
                        _otherKeyPressedWhileTriggerDown = false;
                        _triggerKeyDownTick = now;
                        triggered = true;
                        FireTriggerEvent(isSimulation);
                    }

                    // Не вызываем UpdateWordBufferOnKeyDown, чтобы сохранить слово и цикл переключения
                    return true;
                }

                if (!_isTriggerKeyDown)
                {
                    _isTriggerKeyDown = true;
                    _triggerKeyDownTick = now;
                    _otherKeyPressedWhileTriggerDown = false;

                    if (_tapCount == 1 && (now - _firstTapUpTick) > MaxDoubleTapIntervalMs)
                    {
                        _tapCount = 0;
                    }
                }

                // На втором нажатии обычной не-модификаторной клавиши при двойном тапе сохраняем буфер слова
                return false;
            }

            if (_isTriggerKeyDown)
            {
                _otherKeyPressedWhileTriggerDown = true;
            }

            // Не сбрасываем счётчик двойного тапа, если пользователь нажимает модификатор из самой комбинации триггера
            uint vkModBit = HotkeyFormatter.GetModifierBit(vkCode);
            bool isRequiredModifierKey = vkModBit != 0 && (_triggerModifiers & vkModBit) != 0;
            if (!isRequiredModifierKey)
            {
                _tapCount = 0;
            }

            if (!IsPaused && !isSimulation)
            {
                UpdateWordBufferOnKeyDown(vkCode, scanCode);
            }

            return false;
        }

        private bool HandleKeyUpCore(uint vkCode, long now, bool isSimulation, out bool triggered)
        {
            triggered = false;

            if (IsRecordingTrigger || !MatchesTriggerKey(vkCode))
            {
                return false;
            }

            if (_suppressNextTriggerKeyUp)
            {
                _isTriggerKeyDown = false;
                _suppressNextTriggerKeyUp = false;
                return true;
            }

            if (IsPaused)
            {
                ResetTriggerTapState();
                return false;
            }

            bool wasDown = _isTriggerKeyDown;
            long holdDuration = now - _triggerKeyDownTick;
            bool hadOtherKey = _otherKeyPressedWhileTriggerDown;

            _isTriggerKeyDown = false;
            _otherKeyPressedWhileTriggerDown = false;

            if (!wasDown || hadOtherKey || holdDuration > MaxTapHoldMs)
            {
                _tapCount = 0;
                return false;
            }

            // Случай одиночного нажатия чистой клавиши-модификатора (например Right Ctrl, Left Alt, Right Alt)
            if (!_triggerRequireDoubleTap)
            {
                _tapCount = 0;
                triggered = true;
                FireTriggerEvent(isSimulation);
                return true;
            }

            // Случай двойного быстрого нажатия любой выбранной клавиши (× 2)
            if (_tapCount == 0)
            {
                _tapCount = 1;
                _firstTapUpTick = now;
                return false;
            }

            if (_tapCount == 1)
            {
                long intervalBetweenTaps = _triggerKeyDownTick - _firstTapUpTick;
                _tapCount = 0;

                if (intervalBetweenTaps >= 0 && intervalBetweenTaps <= MaxDoubleTapIntervalMs)
                {
                    triggered = true;
                    FireTriggerEvent(isSimulation);
                    return true;
                }
            }

            return false;
        }

        private void UpdateWordBufferOnKeyDown(uint vkCode, uint scanCode)
        {
            IntPtr foreground = GetForegroundWindow();
            bool contextChanged = false;

            lock (_bufferLock)
            {
                if (foreground != _lastForegroundHwnd)
                {
                    _lastForegroundHwnd = foreground;
                    ResetWordBufferLocked();
                    contextChanged = true;
                }
            }

            if (contextChanged)
            {
                RaiseInputContextReset();
            }

            if (IsModifierKey(vkCode))
            {
                return;
            }

            bool isRightAltDown = IsKeyDownAsync(VK_RMENU);
            bool isLeftAltDown = IsKeyDownAsync(VK_LMENU);
            bool isLeftCtrlDown = IsKeyDownAsync(VK_LCONTROL);
            bool isRightCtrlDown = IsKeyDownAsync(VK_RCONTROL);
            bool isGenericCtrlDown = IsKeyDownAsync(VK_CONTROL) && !isLeftCtrlDown && !isRightCtrlDown;
            bool isGenericAltDown = IsKeyDownAsync(VK_MENU) && !isLeftAltDown && !isRightAltDown;
            bool isWinDown = IsKeyDownAsync(VK_LWIN) || IsKeyDownAsync(VK_RWIN);

            // AltGr в Windows выглядит как RMENU (+ возможно LCONTROL), не сбрасываем слово при вводе символов с AltGr
            bool isShortcutModifierDown = isWinDown
                || isLeftAltDown
                || isRightCtrlDown
                || (isLeftCtrlDown && !isRightAltDown)
                || (isGenericCtrlDown && !isRightAltDown)
                || (isGenericAltDown && !isRightAltDown);

            if (isShortcutModifierDown)
            {
                ResetTrackedWord();
                RaiseInputContextReset();
                return;
            }

            // Enter завершает слово и запускает конвейер автоматической обработки
            if (vkCode == VK_RETURN)
            {
                string? wordToProcess = null;
                bool isBurst = false;
                lock (_bufferLock)
                {
                    if (_typedWordBuffer.Length > 0 && _trailingSpaceCount == 0)
                    {
                        wordToProcess = _typedWordBuffer.ToString();
                        isBurst = IsBurstTypingLocked();
                    }

                    ResetWordBufferLocked();
                }

                RaiseInputContextReset();
                if (!string.IsNullOrEmpty(wordToProcess))
                {
                    RaiseWordDelimiterTyped(wordToProcess, '\n', isEnter: true, foreground, isBurst);
                }

                return;
            }

            if (vkCode == VK_TAB
                || vkCode == VK_ESCAPE
                || vkCode == VK_DELETE
                || vkCode == VK_INSERT
                || vkCode == VK_APPS
                || vkCode == VK_PAUSE
                || (vkCode >= VK_PRIOR && vkCode <= VK_DOWN))
            {
                ResetTrackedWord();
                RaiseInputContextReset();
                return;
            }

            RaiseInputContextReset();

            if (vkCode == VK_BACK)
            {
                lock (_bufferLock)
                {
                    if (_trailingSpaceCount > 0)
                    {
                        _trailingSpaceCount--;
                    }
                    else if (_trackedWordLength > 0)
                    {
                        _trackedWordLength--;
                        if (_typedWordBuffer.Length > 0)
                        {
                            _typedWordBuffer.Length--;
                        }
                    }
                }

                return;
            }

            if (vkCode == VK_SPACE)
            {
                string? wordToProcess = null;
                bool isBurst = false;
                lock (_bufferLock)
                {
                    if (_typedWordBuffer.Length > 0 && _trailingSpaceCount == 0)
                    {
                        wordToProcess = _typedWordBuffer.ToString();
                        isBurst = IsBurstTypingLocked();
                    }

                    if (_trackedWordLength > 0)
                    {
                        _trailingSpaceCount++;
                        if (_trailingSpaceCount > 2)
                        {
                            ResetWordBufferLocked();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(wordToProcess))
                {
                    RaiseWordDelimiterTyped(wordToProcess, ' ', isEnter: false, foreground, isBurst);
                }

                return;
            }

            if (IsLayoutPrintableVirtualKey(vkCode))
            {
                long keyTimestamp = Stopwatch.GetTimestamp();
                char? typedChar = TranslateVkToChar(vkCode, scanCode, foreground);
                string? windowLang = GetWindowKeyboardLanguage(foreground);

                if (typedChar.HasValue && IsPunctuationDelimiterCandidate(typedChar.Value))
                {
                    bool isRuOrUkWindow = !string.IsNullOrEmpty(windowLang)
                        && (string.Equals(windowLang, UniversalLayoutService.RussianKlid, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(windowLang, UniversalLayoutService.UkrainianKlid, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(windowLang, UniversalLayoutService.UkrainianEnhancedKlid, StringComparison.OrdinalIgnoreCase));

                    bool treatAsDelimiter = typedChar.Value == '!' || isRuOrUkWindow;

                    if (!treatAsDelimiter && ShouldTreatPunctuationAsDelimiter != null)
                    {
                        string currentWordSnapshot;
                        lock (_bufferLock)
                        {
                            currentWordSnapshot = _trailingSpaceCount == 0 ? _typedWordBuffer.ToString() : string.Empty;
                        }

                        if (!string.IsNullOrEmpty(currentWordSnapshot))
                        {
                            treatAsDelimiter = ShouldTreatPunctuationAsDelimiter(currentWordSnapshot, typedChar.Value, windowLang);
                        }
                    }

                    if (treatAsDelimiter)
                    {
                        string? wordToProcess = null;
                        bool isBurst = false;
                        lock (_bufferLock)
                        {
                            if (_typedWordBuffer.Length > 0 && _trailingSpaceCount == 0)
                            {
                                wordToProcess = _typedWordBuffer.ToString();
                                isBurst = IsBurstTypingLocked();
                            }

                            ResetWordBufferLocked();
                        }

                        if (!string.IsNullOrEmpty(wordToProcess))
                        {
                            RaiseWordDelimiterTyped(wordToProcess, typedChar.Value, isEnter: false, foreground, isBurst);
                        }

                        return;
                    }
                }

                lock (_bufferLock)
                {
                    if (_trailingSpaceCount > 0)
                    {
                        ResetWordBufferLocked();
                    }

                    if (_lastKeyDownTimestamp > 0)
                    {
                        double intervalMs = (keyTimestamp - _lastKeyDownTimestamp) * 1000.0 / Stopwatch.Frequency;
                        _intervalSumMs += intervalMs;
                        _intervalCount++;
                        if (intervalMs < _minIntervalMs)
                        {
                            _minIntervalMs = intervalMs;
                        }
                    }

                    _lastKeyDownTimestamp = keyTimestamp;

                    if (_trackedWordLength < MaxTrackedWordLength)
                    {
                        _trackedWordLength++;
                        if (typedChar.HasValue)
                        {
                            _typedWordBuffer.Append(typedChar.Value);
                        }
                    }
                }
            }
            else
            {
                ResetTrackedWord();
            }
        }

        private void RaiseWordDelimiterTyped(string word, char delimiter, bool isEnter, IntPtr foreground, bool isBurstTyping)
        {
            string? windowLang = GetWindowKeyboardLanguage(foreground);
            var args = new WordDelimiterEventArgs(word, delimiter, isEnter, windowLang, foreground, isBurstTyping);

            _dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => WordDelimiterTyped?.Invoke(this, args)));
        }

        private static char? TranslateVkToChar(uint vkCode, uint scanCode, IntPtr foregroundHwnd)
        {
            uint threadId = foregroundHwnd != IntPtr.Zero ? GetWindowThreadProcessId(foregroundHwnd, out _) : 0;
            IntPtr hkl = threadId != 0 ? GetKeyboardLayout(threadId) : IntPtr.Zero;

            byte[] keyState = new byte[256];
            if (IsKeyDownAsync(VK_SHIFT) || IsKeyDownAsync(VK_LSHIFT) || IsKeyDownAsync(VK_RSHIFT))
            {
                keyState[VK_SHIFT] = 0x80;
            }

            if (IsKeyDownAsync(VK_RMENU))
            {
                keyState[VK_CONTROL] = 0x80;
                keyState[VK_MENU] = 0x80;
                keyState[VK_RMENU] = 0x80;
            }

            if ((GetKeyState((int)VK_CAPITAL) & 0x0001) != 0)
            {
                keyState[VK_CAPITAL] = 0x01;
            }

            var sb = new StringBuilder(4);
            int count = ToUnicodeEx(
                vkCode,
                scanCode,
                keyState,
                sb,
                sb.Capacity,
                TOUNICODE_DO_NOT_CHANGE_STATE,
                hkl);

            if (count == 1 && sb.Length >= 1)
            {
                return sb[0];
            }

            return null;
        }

        private static bool IsPunctuationDelimiterCandidate(char ch)
        {
            return ch == '.'
                || ch == ','
                || ch == '!'
                || ch == '?'
                || ch == ';'
                || ch == ':';
        }

        private void ResetTriggerTapState()
        {
            _isTriggerKeyDown = false;
            _otherKeyPressedWhileTriggerDown = false;
            _suppressNextTriggerKeyUp = false;
            _tapCount = 0;
        }

        private void RaiseInputContextReset()
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() => InputContextReset?.Invoke(this, EventArgs.Empty)));
        }

        private static bool IsModifierKey(uint vkCode)
        {
            return vkCode == VK_SHIFT
                || vkCode == VK_LSHIFT
                || vkCode == VK_RSHIFT
                || vkCode == VK_CONTROL
                || vkCode == VK_LCONTROL
                || vkCode == VK_RCONTROL
                || vkCode == VK_MENU
                || vkCode == VK_LMENU
                || vkCode == VK_RMENU
                || vkCode == VK_LWIN
                || vkCode == VK_RWIN;
        }

        private static bool IsLayoutPrintableVirtualKey(uint vkCode)
        {
            if (vkCode >= '0' && vkCode <= '9')
            {
                return true;
            }

            if (vkCode >= 'A' && vkCode <= 'Z')
            {
                return true;
            }

            return (vkCode >= VK_OEM_1 && vkCode <= VK_OEM_3)
                || (vkCode >= VK_OEM_4 && vkCode <= VK_OEM_8)
                || vkCode == VK_OEM_102;
        }

        private static bool IsKeyDownAsync(uint vkCode)
        {
            return (GetAsyncKeyState((int)vkCode) & 0x8000) != 0;
        }

        #region Win32 P/Invoke

        /// <summary>
        /// Устанавливает процедуру-перехватчик (хук) в глобальную цепочку обработки событий ввода Windows.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        /// <summary>
        /// Удаляет ранее установленный хук из цепочки Windows.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        /// <summary>
        /// Передаёт событие ввода следующему обработчику в цепочке хуков Windows.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Возвращает дескриптор загруженного модуля текущего процесса для регистрации глобального хука.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        /// <summary>
        /// Возвращает дескриптор (HWND) текущего активного окна переднего плана.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Получает идентификатор потока указанного окна для определения его раскладки клавиатуры.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

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
        /// Возвращает сведения о потоке GUI и дочернем окне фокуса ввода (для корректного чтения HKL в UWP/WinUI).
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        /// <summary>
        /// Возвращает активную раскладку клавиатуры (HKL) для указанного потока Windows.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        /// <summary>
        /// Преобразует виртуальный код клавиши и скан-код в символ Unicode с учётом раскладки окна, Shift и Caps Lock.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(
            uint wVirtKey,
            uint wScanCode,
            byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
            int cchBuff,
            uint wFlags,
            IntPtr dwhkl);

        /// <summary>
        /// Проверяет текущее физическое состояние клавиши (старший бит 0x8000 означает, что клавиша нажата).
        /// </summary>
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// Проверяет состояние переключателя (например, Caps Lock — младший бит 0x0001).
        /// </summary>
        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        #endregion
    }
}
