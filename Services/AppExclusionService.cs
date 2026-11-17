using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Состояние проверки активного окна по чёрному списку программ и полноэкранному режиму.
    /// </summary>
    public sealed class ExclusionCheckResult
    {
        public bool IsAutomaticPaused { get; }
        public bool IsManualBlocked { get; }
        public string? ProcessName { get; }
        public bool IsBlacklisted { get; }
        public bool IsFullscreen { get; }

        public static readonly ExclusionCheckResult Active =
            new ExclusionCheckResult(false, false, null, false, false);

        public ExclusionCheckResult(
            bool isAutomaticPaused,
            bool isManualBlocked,
            string? processName,
            bool isBlacklisted,
            bool isFullscreen)
        {
            IsAutomaticPaused = isAutomaticPaused;
            IsManualBlocked = isManualBlocked;
            ProcessName = processName;
            IsBlacklisted = isBlacklisted;
            IsFullscreen = isFullscreen;
        }
    }

    /// <summary>
    /// Информация об открытом оконном процессе для быстрого добавления в чёрный список.
    /// </summary>
    public sealed class RunningWindowAppInfo
    {
        public string ExecutableName { get; }
        public string WindowTitle { get; }

        public RunningWindowAppInfo(string executableName, string windowTitle)
        {
            ExecutableName = executableName;
            WindowTitle = windowTitle;
        }

        public string DisplayLabel => string.IsNullOrWhiteSpace(WindowTitle)
            ? ExecutableName
            : $"{ExecutableName} — {WindowTitle}";
    }

    /// <summary>
    /// Отвечает за Шаг 0 конвейера: проверку чёрного списка процессов (app_blacklist.json)
    /// и автоматическое определение полноэкранных игр/приложений строго при включённом флаге EnableAppExclusions.
    /// </summary>
    public sealed class AppExclusionService
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;

        // Системные классы оболочки Windows, которые не должны считаться полноэкранным приложением
        private static readonly HashSet<string> DesktopShellClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow",
            "Windows.UI.Core.CoreWindow"
        };

        private readonly SettingsManager _settingsManager;
        private readonly object _lock = new object();
        private readonly HashSet<string> _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AppExclusionService(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            ReloadFromDisk();
        }

        public void ReloadFromDisk()
        {
            List<string> loaded = _settingsManager.LoadAppBlacklist();
            lock (_lock)
            {
                _blacklist.Clear();
                foreach (string item in loaded)
                {
                    string normalized = NormalizeExeName(item);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _blacklist.Add(normalized);
                    }
                }
            }
        }

        public List<string> GetBlacklistSnapshot()
        {
            lock (_lock)
            {
                return _blacklist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _blacklist.Count;
                }
            }
        }

        public bool AddExecutable(string rawInput)
        {
            string normalized = NormalizeExeName(rawInput);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            lock (_lock)
            {
                if (_blacklist.Add(normalized))
                {
                    SaveLocked();
                    return true;
                }
            }

            return false;
        }

        public bool RemoveExecutable(string exeName)
        {
            string normalized = NormalizeExeName(exeName);
            lock (_lock)
            {
                if (_blacklist.Remove(normalized))
                {
                    SaveLocked();
                    return true;
                }
            }

            return false;
        }

        public void ReplaceBlacklist(IEnumerable<string> executables)
        {
            lock (_lock)
            {
                _blacklist.Clear();
                foreach (string item in executables)
                {
                    string normalized = NormalizeExeName(item);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _blacklist.Add(normalized);
                    }
                }

                SaveLocked();
            }
        }

        public void ClearBlacklist()
        {
            lock (_lock)
            {
                _blacklist.Clear();
                SaveLocked();
            }
        }

        /// <summary>
        /// Проверяет текущее активное окно Windows (Шаг 0).
        /// Если главная галочка EnableAppExclusions снята — немедленно возвращает разрешение без проверок.
        /// </summary>
        public ExclusionCheckResult EvaluateForegroundWindow(AppSettings settings)
        {
            if (settings == null || !settings.EnableAppExclusions)
            {
                return ExclusionCheckResult.Active;
            }

            IntPtr foregroundHwnd = GetForegroundWindow();
            if (foregroundHwnd == IntPtr.Zero)
            {
                return ExclusionCheckResult.Active;
            }

            string? processName = GetWindowProcessExecutableName(foregroundHwnd);
            bool isFullscreen = settings.AutoPauseInFullscreen && IsWindowFullscreen(foregroundHwnd);

            return EvaluateState(settings, processName, isFullscreen);
        }

        /// <summary>
        /// Выполняет логическую оценку исключений по имени процесса и состоянию полноэкранного режима.
        /// </summary>
        public ExclusionCheckResult EvaluateState(AppSettings settings, string? processName, bool isFullscreen)
        {
            if (settings == null || !settings.EnableAppExclusions)
            {
                return ExclusionCheckResult.Active;
            }

            bool isBlacklisted = false;
            if (!string.IsNullOrWhiteSpace(processName))
            {
                string normalized = NormalizeExeName(processName);
                lock (_lock)
                {
                    isBlacklisted = _blacklist.Contains(normalized);
                }
            }

            bool effectiveFullscreen = settings.AutoPauseInFullscreen && isFullscreen;
            bool excluded = isBlacklisted || effectiveFullscreen;

            if (!excluded)
            {
                return new ExclusionCheckResult(false, false, processName, false, false);
            }

            return new ExclusionCheckResult(
                isAutomaticPaused: true,
                isManualBlocked: settings.BlockManualTriggerInExcluded,
                processName: processName,
                isBlacklisted: isBlacklisted,
                isFullscreen: effectiveFullscreen);
        }

        /// <summary>
        /// Возвращает список текущих открытых оконных программ для выбора в чёрный список.
        /// </summary>
        public List<RunningWindowAppInfo> GetRunningWindowProcesses()
        {
            var seenExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var apps = new List<RunningWindowAppInfo>();
            int currentPid = Environment.ProcessId;

            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd))
                {
                    return true;
                }

                string className = GetWindowClassName(hwnd);
                if (DesktopShellClasses.Contains(className))
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == currentPid)
                {
                    return true;
                }

                int titleLen = GetWindowTextLength(hwnd);
                if (titleLen <= 0)
                {
                    return true;
                }

                string? exe = GetProcessExecutableNameByPid(pid);
                if (string.IsNullOrWhiteSpace(exe) || !seenExes.Add(exe))
                {
                    return true;
                }

                var sbTitle = new StringBuilder(titleLen + 1);
                GetWindowText(hwnd, sbTitle, sbTitle.Capacity);
                apps.Add(new RunningWindowAppInfo(exe, sbTitle.ToString().Trim()));
                return true;
            }, IntPtr.Zero);

            return apps
                .OrderBy(a => a.ExecutableName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string NormalizeExeName(string? rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                return string.Empty;
            }

            string trimmed = rawInput.Trim().Trim('"');
            try
            {
                if (trimmed.Contains('\\') || trimmed.Contains('/'))
                {
                    trimmed = Path.GetFileName(trimmed);
                }
            }
            catch
            {
                // Оставляем исходную строку при некорректных символах пути
            }

            trimmed = trimmed.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(trimmed))
            {
                return string.Empty;
            }

            if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                trimmed += ".exe";
            }

            return trimmed;
        }

        private static string? GetWindowProcessExecutableName(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                return null;
            }

            return GetProcessExecutableNameByPid(pid);
        }

        private static string? GetProcessExecutableNameByPid(uint pid)
        {
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var buffer = new StringBuilder(1024);
                int capacity = buffer.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, buffer, ref capacity))
                {
                    return Path.GetFileName(buffer.ToString());
                }
            }
            catch
            {
                // Игнорируем защищённые системные процессы
            }
            finally
            {
                CloseHandle(hProcess);
            }

            return null;
        }

        private static bool IsWindowFullscreen(IntPtr hwnd)
        {
            string className = GetWindowClassName(hwnd);
            if (DesktopShellClasses.Contains(className))
            {
                return false;
            }

            // 1. Проверка состояния оболочки Windows (Direct3D fullscreen / Busy / Presentation)
            if (SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state) == 0)
            {
                if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                    || state == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY
                    || state == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                {
                    return true;
                }
            }

            // 2. Проверка Borderless Fullscreen по границам окна и монитора
            if (!GetWindowRect(hwnd, out RECT winRect))
            {
                return false;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var monInfo = new MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFO>()
            };

            if (!GetMonitorInfo(monitor, ref monInfo))
            {
                return false;
            }

            RECT monRect = monInfo.rcMonitor;
            bool coversMonitor = winRect.Left <= monRect.Left
                && winRect.Top <= monRect.Top
                && winRect.Right >= monRect.Right
                && winRect.Bottom >= monRect.Bottom;

            if (!coversMonitor)
            {
                return false;
            }

            // Обычное развёрнутое окно с заголовком при скрытой панели задач не считаем игрой
            int style = GetWindowLong(hwnd, GWL_STYLE);
            bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
            if (hasCaption && IsZoomed(hwnd))
            {
                return false;
            }

            return true;
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private void SaveLocked()
        {
            List<string> snapshot = _blacklist.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            _settingsManager.SaveAppBlacklist(snapshot);
        }

        #region Win32 P/Invoke

        private enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// Возвращает дескриптор текущего активного окна переднего плана.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Возвращает идентификатор процесса (PID) по дескриптору окна.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// Открывает дескриптор процесса с минимальными правами PROCESS_QUERY_LIMITED_INFORMATION (0x1000),
        /// что позволяет получать имя EXE даже для процессов администратора и игр с античитом без AccessDenied.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        /// <summary>
        /// Получает полный путь к исполняемому файлу процесса по его ограниченному дескриптору.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        /// <summary>
        /// Закрывает открытый дескриптор ядра Windows.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Запрашивает состояние пользовательских уведомлений Windows (определение полноэкранных Direct3D-игр и режима презентации).
        /// </summary>
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        /// <summary>
        /// Возвращает экранные координаты прямоугольника окна.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        /// <summary>
        /// Возвращает дескриптор монитора, на котором преимущественно расположено окно.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        /// <summary>
        /// Получает геометрию и рабочую область указанного монитора.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>
        /// Возвращает имя оконного класса для фильтрации системных окон рабочего стола.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>
        /// Перечисляет все окна верхнего уровня для списка запущенных программ.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        /// <summary>
        /// Проверяет видимость окна на экране.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>
        /// Возвращает длину заголовка окна.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>
        /// Читает заголовок окна.
        /// </summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        /// <summary>
        /// Получает стиль окна для отличия обычного развёрнутого окна от полноэкранного режима без рамок.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        /// <summary>
        /// Проверяет, развёрнуто ли окно на весь экран как обычное окно Windows.
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hWnd);

        #endregion
    }
}
