using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LanguageSwitcher.Models;
using Microsoft.Win32;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Динамически построенная с помощью Win32 ToUnicodeEx карта символов и матрица физических координат клавиш
    /// для произвольной раскладки клавиатуры Windows (KLID).
    /// </summary>
    public sealed class DynamicLayoutProfile
    {
        public const int KeysPerModifierState = 48;
        public const int ModifierStateCount = 4; // 0: Normal, 1: Shift, 2: AltGr, 3: Shift+AltGr
        public const int TotalSlotCount = KeysPerModifierState * ModifierStateCount; // 192
        private const int FastCharIndexLimit = 2048;

        public string Klid { get; }
        public KeyboardLayoutInfo Info { get; }
        public char[] CharBySlot { get; }
        public Dictionary<char, int> IndexByChar { get; }
        public Dictionary<char, (double Row, double Col)> KeyPositions { get; }
        public HashSet<char> AlphabetChars { get; }

        private readonly short[] _fastCharToNode;
        private readonly Dictionary<char, int> _extendedCharToNode;
        private readonly bool[] _adjacencyMatrix;
        private readonly int _nodeCount;

        public DynamicLayoutProfile(
            string klid,
            KeyboardLayoutInfo info,
            char[] charBySlot,
            Dictionary<char, int> indexByChar,
            Dictionary<char, (double Row, double Col)> keyPositions,
            HashSet<char> alphabetChars)
        {
            Klid = klid;
            Info = info;
            CharBySlot = charBySlot;
            IndexByChar = indexByChar;
            KeyPositions = keyPositions;
            AlphabetChars = alphabetChars;

            _fastCharToNode = new short[FastCharIndexLimit];
            Array.Fill(_fastCharToNode, (short)-1);
            _extendedCharToNode = new Dictionary<char, int>();

            var nodeChars = new List<char>(keyPositions.Count);
            foreach (char ch in keyPositions.Keys)
            {
                int nodeIdx = nodeChars.Count;
                nodeChars.Add(ch);
                if (ch < FastCharIndexLimit)
                {
                    _fastCharToNode[ch] = (short)nodeIdx;
                }
                else
                {
                    _extendedCharToNode[ch] = nodeIdx;
                }
            }

            _nodeCount = nodeChars.Count;
            _adjacencyMatrix = new bool[_nodeCount * _nodeCount];

            for (int i = 0; i < _nodeCount; i++)
            {
                (double rowA, double colA) = keyPositions[nodeChars[i]];
                int rowOffset = i * _nodeCount;

                for (int j = 0; j < _nodeCount; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }

                    (double rowB, double colB) = keyPositions[nodeChars[j]];
                    double dr = rowA - rowB;
                    double dc = colA - colB;
                    if ((dr * dr) + (dc * dc) <= 2.15)
                    {
                        _adjacencyMatrix[rowOffset + j] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Возвращает true, если клавиши символов a и b физически соседствуют на данной раскладке
        /// (по горизонтали, вертикали или диагонали).
        /// </summary>
        public bool AreKeysAdjacent(char a, char b)
        {
            if (a == b || _nodeCount == 0)
            {
                return false;
            }

            int idxA = GetNodeIndex(a);
            if (idxA < 0)
            {
                return false;
            }

            int idxB = GetNodeIndex(b);
            if (idxB < 0)
            {
                return false;
            }

            return _adjacencyMatrix[(idxA * _nodeCount) + idxB];
        }

        private int GetNodeIndex(char ch)
        {
            if (ch < FastCharIndexLimit)
            {
                return _fastCharToNode[ch];
            }

            return _extendedCharToNode.TryGetValue(ch, out int idx) ? idx : -1;
        }
    }

    /// <summary>
    /// Универсальный движок раскладок мира на базе Win32 API (GetKeyboardLayoutList, LoadKeyboardLayout, ToUnicodeEx)
    /// и системного каталога HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts.
    /// </summary>
    public sealed class UniversalLayoutService
    {
        public const string EnglishUsKlid = "00000409";
        public const string RussianKlid = "00000419";
        public const string UkrainianKlid = "00000422";
        public const string UkrainianEnhancedKlid = "00020422";

        private const uint KLF_NOTELLSHELL = 0x00000080;
        private const uint MAPVK_VSC_TO_VK = 1;
        private const uint MAPVK_VSC_TO_VK_EX = 3;
        private const uint TOUNICODE_DO_NOT_CHANGE_STATE = 0x00000004;

        private const string KeyboardLayoutsRegistryPath = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

        private readonly struct PhysicalKeyDef
        {
            public uint ScanCode { get; }
            public double Row { get; }
            public double Col { get; }

            public PhysicalKeyDef(uint scanCode, double row, double col)
            {
                ScanCode = scanCode;
                Row = row;
                Col = col;
            }
        }

        // 48 физических клавиш стандартной 102/104/105-клавишной клавиатуры в порядке скан-кодов
        private static readonly PhysicalKeyDef[] PhysicalKeys = BuildPhysicalKeys();

        private static readonly HashSet<char> LatinAlphabetChars = new HashSet<char>("abcdefghijklmnopqrstuvwxyz");

        public static UniversalLayoutService Instance { get; } = new UniversalLayoutService();

        private readonly object _catalogLock = new object();
        private readonly Dictionary<string, KeyboardLayoutInfo> _catalog =
            new Dictionary<string, KeyboardLayoutInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliasToKlid =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ushort, string> _layoutIdToKlid = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, string> _primaryLangToKlid = new Dictionary<ushort, string>();

        private readonly ConcurrentDictionary<string, DynamicLayoutProfile> _profiles =
            new ConcurrentDictionary<string, DynamicLayoutProfile>(StringComparer.OrdinalIgnoreCase);

        private UniversalLayoutService()
        {
            InitializeCatalog();
        }

        private void InitializeCatalog()
        {
            // 1. Встроенный каталог мировых раскладок (45+ раскладок всех регионов)
            KeyboardLayoutInfo[] curated =
            {
                // Европейские
                CreateCatalogInfo("00000409", "en-US", "English (US)", "English (United States)", "EN"),
                CreateCatalogInfo("00000809", "en-GB", "English (UK)", "English (United Kingdom)", "GB"),
                CreateCatalogInfo("00000407", "de-DE", "Deutsch (QWERTZ)", "Deutsch (Deutschland)", "DE"),
                CreateCatalogInfo("00000807", "de-CH", "Deutsch (Schweiz)", "Deutsch (Schweiz)", "DE"),
                CreateCatalogInfo("0000040C", "fr-FR", "Français (AZERTY)", "Français (France)", "FR"),
                CreateCatalogInfo("00000C0C", "fr-CA", "Français (Canada)", "Français (Canada)", "FR"),
                CreateCatalogInfo("0000040A", "es-ES", "Español", "Español (España)", "ES"),
                CreateCatalogInfo("0000080A", "es-MX", "Español (Latinoamérica)", "Español (Latinoamérica)", "ES"),
                CreateCatalogInfo("00000410", "it-IT", "Italiano", "Italiano (Italia)", "IT"),
                CreateCatalogInfo("00000816", "pt-PT", "Português (Portugal)", "Português (Portugal)", "PT"),
                CreateCatalogInfo("00000416", "pt-BR", "Português (Brasil)", "Português (Brasil)", "BR"),
                CreateCatalogInfo("00000415", "pl-PL", "Polski (Programisty)", "Polski (Polska)", "PL"),
                CreateCatalogInfo("00000413", "nl-NL", "Nederlands", "Nederlands (Nederland)", "NL"),
                CreateCatalogInfo("00000405", "cs-CZ", "Čeština (QWERTZ)", "Čeština (Česko)", "CS"),
                CreateCatalogInfo("0000041B", "sk-SK", "Slovenčina", "Slovenčina (Slovensko)", "SK"),
                CreateCatalogInfo("0000040E", "hu-HU", "Magyar", "Magyar (Magyarország)", "HU"),
                CreateCatalogInfo("00000418", "ro-RO", "Română", "Română (România)", "RO"),
                CreateCatalogInfo("0000041D", "sv-SE", "Svenska", "Svenska (Sverige)", "SV"),
                CreateCatalogInfo("00000406", "da-DK", "Dansk", "Dansk (Danmark)", "DA"),
                CreateCatalogInfo("00000414", "nb-NO", "Norsk", "Norsk bokmål (Norge)", "NO"),
                CreateCatalogInfo("0000040B", "fi-FI", "Suomi", "Suomi (Suomi)", "FI"),
                CreateCatalogInfo("0000041F", "tr-TR", "Türkçe (Q)", "Türkçe (Q)", "TR"),
                CreateCatalogInfo("0001041F", "tr-TR", "Türkçe (F)", "Türkçe (F)", "TRF"),
                CreateCatalogInfo("00000408", "el-GR", "Ελληνικά (Greek)", "Ελληνικά (Ελλάδα)", "EL"),
                CreateCatalogInfo("00000425", "et-EE", "Eesti", "Eesti (Eesti)", "ET"),
                CreateCatalogInfo("00000426", "lv-LV", "Latviešu", "Latviešu (Latvija)", "LV"),
                CreateCatalogInfo("00000427", "lt-LT", "Lietuvių", "Lietuvių (Lietuva)", "LT"),
                CreateCatalogInfo("0000041A", "hr-HR", "Hrvatski", "Hrvatski (Hrvatska)", "HR"),
                CreateCatalogInfo("00000424", "sl-SI", "Slovenščina", "Slovenščina (Slovenija)", "SL"),
                CreateCatalogInfo("0000040F", "is-IS", "Íslenska", "Íslenska (Ísland)", "IS"),
                CreateCatalogInfo("00001809", "en-IE", "Gaeilge / English (IE)", "English (Ireland)", "IE"),

                // Кириллические и СНГ
                CreateCatalogInfo("00000419", "ru-RU", "Русский", "Русский (Россия)", "RU"),
                CreateCatalogInfo("00000422", "uk-UA", "Українська", "Українська (Україна)", "UA"),
                CreateCatalogInfo("00020422", "uk-UA", "Українська (розширена)", "Українська (розширена)", "UA+"),
                CreateCatalogInfo("00000423", "be-BY", "Беларуская", "Беларуская (Беларусь)", "BY"),
                CreateCatalogInfo("0000043F", "kk-KZ", "Қазақша", "Қазақ (Қазақстан)", "KZ"),
                CreateCatalogInfo("00030402", "bg-BG", "Български (БДС)", "Български (БДС)", "BG"),
                CreateCatalogInfo("00020402", "bg-BG", "Български (Фонетична)", "Български (Фонетична)", "BG"),
                CreateCatalogInfo("00000C1A", "sr-Cyrl-RS", "Српски (Кирилица)", "Српски (Србија)", "SR"),
                CreateCatalogInfo("0000042F", "mk-MK", "Македонски", "Македонски", "MK"),
                CreateCatalogInfo("00000440", "ky-KG", "Кыргызча", "Кыргызча", "KG"),
                CreateCatalogInfo("00000443", "uz-Cyrl-UZ", "Ўзбекча (Кирилица)", "Ўзбекча", "UZ"),
                CreateCatalogInfo("0000042C", "az-Latn-AZ", "Azərbaycan", "Azərbaycan (Azərbaycan)", "AZ"),

                // Ближний Восток и Азия
                CreateCatalogInfo("00000401", "ar-SA", "العربية (Arabic)", "العربية", "AR"),
                CreateCatalogInfo("0000040D", "he-IL", "עברית (Hebrew)", "עברית", "HE"),
                CreateCatalogInfo("00000429", "fa-IR", "فارسی (Persian)", "فارسی", "FA"),
                CreateCatalogInfo("00000437", "ka-GE", "ქართული (Georgian)", "ქართული", "KA"),
                CreateCatalogInfo("0000042B", "hy-AM", "Հայերեն (Armenian)", "Հայերեն", "HY"),
                CreateCatalogInfo("00010439", "hi-IN", "हिन्दी (Hindi)", "हिन्दी", "HI"),
                CreateCatalogInfo("0000041E", "th-TH", "ไทย (Thai)", "ไทย", "TH"),
                CreateCatalogInfo("0000042A", "vi-VN", "Tiếng Việt", "Tiếng Việt", "VI"),
                CreateCatalogInfo("00000412", "ko-KR", "한국어 (Korean)", "한국어", "KO"),
                CreateCatalogInfo("00000411", "ja-JP", "日本語 (Japanese)", "日本語", "JA"),
                CreateCatalogInfo("00000804", "zh-CN", "中文 (简体)", "中文 (中国)", "ZH")
            };

            lock (_catalogLock)
            {
                foreach (KeyboardLayoutInfo info in curated)
                {
                    RegisterCatalogEntryLocked(info, overwriteExisting: true);
                }

                // Алиасы обратной совместимости (старые enum SupportedLanguage и коды культур)
                _aliasToKlid["English"] = EnglishUsKlid;
                _aliasToKlid["EN"] = EnglishUsKlid;
                _aliasToKlid["en"] = EnglishUsKlid;
                _aliasToKlid["en-US"] = EnglishUsKlid;

                _aliasToKlid["Russian"] = RussianKlid;
                _aliasToKlid["RU"] = RussianKlid;
                _aliasToKlid["ru"] = RussianKlid;
                _aliasToKlid["ru-RU"] = RussianKlid;

                _aliasToKlid["Ukrainian"] = UkrainianKlid;
                _aliasToKlid["UK"] = UkrainianKlid;
                _aliasToKlid["UA"] = UkrainianKlid;
                _aliasToKlid["uk"] = UkrainianKlid;
                _aliasToKlid["uk-UA"] = UkrainianKlid;

                // 2. Читаем все раскладки из системного реестра Windows
                LoadRegistryLayoutsLocked();

                // 3. Обновляем флаги раскладок, реально установленных в Windows сейчас
                RefreshInstalledWindowsLayoutsLocked();
            }
        }

        private void RegisterCatalogEntryLocked(KeyboardLayoutInfo info, bool overwriteExisting)
        {
            if (overwriteExisting || !_catalog.ContainsKey(info.Klid))
            {
                _catalog[info.Klid] = info;
            }

            if (!_aliasToKlid.ContainsKey(info.CultureCode))
            {
                _aliasToKlid[info.CultureCode] = info.Klid;
            }

            if (!_aliasToKlid.ContainsKey(info.IsoLanguageCode))
            {
                _aliasToKlid[info.IsoLanguageCode] = info.Klid;
            }

            if (!_aliasToKlid.ContainsKey(info.ShortBadge))
            {
                _aliasToKlid[info.ShortBadge] = info.Klid;
            }

            if (!_primaryLangToKlid.ContainsKey(info.LanguageId))
            {
                _primaryLangToKlid[info.LanguageId] = info.Klid;
            }
        }

        private void LoadRegistryLayoutsLocked()
        {
            try
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(KeyboardLayoutsRegistryPath, writable: false);
                if (root == null)
                {
                    return;
                }

                foreach (string subKeyName in root.GetSubKeyNames())
                {
                    if (subKeyName.Length != 8 || !uint.TryParse(subKeyName, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint klidVal))
                    {
                        continue;
                    }

                    string klid = subKeyName.ToUpperInvariant();
                    ushort langId = (ushort)(klidVal & 0xFFFF);

                    using RegistryKey? sub = root.OpenSubKey(subKeyName, writable: false);
                    if (sub == null)
                    {
                        continue;
                    }

                    string? layoutIdHex = sub.GetValue("Layout Id") as string;
                    if (!string.IsNullOrWhiteSpace(layoutIdHex)
                        && ushort.TryParse(layoutIdHex.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort layoutIdVal))
                    {
                        _layoutIdToKlid[layoutIdVal] = klid;
                    }

                    if (_catalog.ContainsKey(klid))
                    {
                        continue;
                    }

                    string? layoutText = sub.GetValue("Layout Text") as string;
                    KeyboardLayoutInfo built = BuildInfoFromLanguageIdAndText(klid, langId, layoutText);
                    RegisterCatalogEntryLocked(built, overwriteExisting: false);
                }
            }
            catch
            {
                // Реестр может быть частично недоступен в ограниченных средах — опираемся на встроенный каталог
            }
        }

        private void RefreshInstalledWindowsLayoutsLocked()
        {
            foreach (KeyboardLayoutInfo item in _catalog.Values)
            {
                item.IsInstalledInWindows = false;
            }

            foreach (string klid in GetInstalledWindowsKlidsInternal())
            {
                if (_catalog.TryGetValue(klid, out KeyboardLayoutInfo? existing))
                {
                    existing.IsInstalledInWindows = true;
                }
                else if (uint.TryParse(klid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint klidVal))
                {
                    KeyboardLayoutInfo generated = BuildInfoFromLanguageIdAndText(klid, (ushort)(klidVal & 0xFFFF), null);
                    generated.IsInstalledInWindows = true;
                    RegisterCatalogEntryLocked(generated, overwriteExisting: true);
                }
            }
        }

        /// <summary>
        /// Нормализует любой идентификатор языка/раскладки (старый enum "English", ISO-код "de",
        /// культуру "fr-FR" или HEX KLID "00000407") к каноническому 8-символьному верхнерегистровому KLID.
        /// </summary>
        public string NormalizeKlid(string? rawLanguageOrKlid)
        {
            if (string.IsNullOrWhiteSpace(rawLanguageOrKlid))
            {
                return EnglishUsKlid;
            }

            string trimmed = rawLanguageOrKlid.Trim();

            lock (_catalogLock)
            {
                if (_aliasToKlid.TryGetValue(trimmed, out string? mapped))
                {
                    return mapped;
                }
            }

            string noPrefix = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring(2)
                : trimmed;

            if (uint.TryParse(noPrefix, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
            {
                string canonical = hexVal.ToString("X8", CultureInfo.InvariantCulture);
                lock (_catalogLock)
                {
                    if (_catalog.ContainsKey(canonical))
                    {
                        return canonical;
                    }

                    ushort langId = (ushort)(hexVal & 0xFFFF);
                    if (hexVal <= 0xFFFF && _primaryLangToKlid.TryGetValue(langId, out string? primaryMatch))
                    {
                        return primaryMatch;
                    }

                    // Регистрируем динамически переданный произвольный 8-значный KLID Windows
                    KeyboardLayoutInfo info = BuildInfoFromLanguageIdAndText(canonical, langId, null);
                    RegisterCatalogEntryLocked(info, overwriteExisting: false);
                    return canonical;
                }
            }

            // Попытка сопоставить с установленной или системной культурой (например "pl-PL", "de-AT", "fr")
            try
            {
                var culture = CultureInfo.GetCultureInfo(trimmed);
                ushort lcid = (ushort)(culture.LCID & 0xFFFF);
                if (lcid != 0 && lcid != 0x1000)
                {
                    lock (_catalogLock)
                    {
                        if (_primaryLangToKlid.TryGetValue(lcid, out string? byLcid))
                        {
                            return byLcid;
                        }
                    }

                    string syntheticKlid = $"0000{lcid:X4}";
                    lock (_catalogLock)
                    {
                        KeyboardLayoutInfo info = BuildInfoFromLanguageIdAndText(syntheticKlid, lcid, null);
                        RegisterCatalogEntryLocked(info, overwriteExisting: false);
                    }
                    return syntheticKlid;
                }
            }
            catch
            {
                // Неизвестный формат строки — фолбэк ниже
            }

            // Поиск по началу названия в каталоге
            lock (_catalogLock)
            {
                KeyboardLayoutInfo? byName = _catalog.Values.FirstOrDefault(v =>
                    string.Equals(v.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(v.NativeName, trimmed, StringComparison.OrdinalIgnoreCase));
                if (byName != null)
                {
                    return byName.Klid;
                }
            }

            return EnglishUsKlid;
        }

        /// <summary>
        /// Возвращает метаданные раскладки клавиатуры по KLID, коду языка или старому названию.
        /// </summary>
        public KeyboardLayoutInfo GetLayoutInfo(string? klidOrAlias)
        {
            string klid = NormalizeKlid(klidOrAlias);
            lock (_catalogLock)
            {
                if (_catalog.TryGetValue(klid, out KeyboardLayoutInfo? info))
                {
                    return info;
                }

                ushort langId = 0x0409;
                if (uint.TryParse(klid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hexVal))
                {
                    langId = (ushort)(hexVal & 0xFFFF);
                }

                KeyboardLayoutInfo created = BuildInfoFromLanguageIdAndText(klid, langId, null);
                RegisterCatalogEntryLocked(created, overwriteExisting: false);
                return created;
            }
        }

        /// <summary>
        /// Возвращает список KLID всех раскладок клавиатуры, установленных у пользователя в Windows.
        /// </summary>
        public List<string> GetInstalledWindowsKlids()
        {
            lock (_catalogLock)
            {
                RefreshInstalledWindowsLayoutsLocked();
                return _catalog.Values
                    .Where(x => x.IsInstalledInWindows)
                    .Select(x => x.Klid)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// Возвращает полный каталог доступных раскладок (установленные в Windows идут первыми, затем мировой каталог и реестр Windows).
        /// </summary>
        public List<KeyboardLayoutInfo> GetAllAvailableLayouts()
        {
            lock (_catalogLock)
            {
                RefreshInstalledWindowsLayoutsLocked();
                return _catalog.Values
                    .OrderByDescending(x => x.IsInstalledInWindows)
                    .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(x => x.Klid, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>
        /// Преобразует дескриптор HKL активного окна Windows в канонический KLID,
        /// учитывая добавленные пользователем раскладки.
        /// </summary>
        public string? ResolveKlidFromHkl(IntPtr hkl, AppSettings? settings = null)
        {
            if (hkl == IntPtr.Zero)
            {
                return null;
            }

            long raw = hkl.ToInt64();
            ushort langId = (ushort)(raw & 0xFFFF);
            ushort hiWord = (ushort)((raw >> 16) & 0xFFFF);

            string? candidate = null;
            lock (_catalogLock)
            {
                if ((hiWord & 0xF000) == 0xF000)
                {
                    ushort layoutId = (ushort)(hiWord & 0x0FFF);
                    if (_layoutIdToKlid.TryGetValue(layoutId, out string? byLayoutId))
                    {
                        candidate = byLayoutId;
                    }
                }
                else if (hiWord != 0)
                {
                    string exact = $"0000{hiWord:X4}";
                    if (_catalog.ContainsKey(exact))
                    {
                        candidate = exact;
                    }
                }

                if (candidate == null && _primaryLangToKlid.TryGetValue(langId, out string? primary))
                {
                    candidate = primary;
                }
            }

            if (candidate == null && langId != 0)
            {
                candidate = $"0000{langId:X4}";
                GetLayoutInfo(candidate);
            }

            if (candidate != null && settings != null)
            {
                if (settings.TargetLanguages.Any(k => string.Equals(k, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return NormalizeKlid(candidate);
                }

                // Если у пользователя добавлена расширенная/альтернативная раскладка для того же языка (например 00020422 при langId 0422)
                foreach (string target in settings.TargetLanguages)
                {
                    KeyboardLayoutInfo info = GetLayoutInfo(target);
                    if (info.LanguageId == langId)
                    {
                        return info.Klid;
                    }
                }
            }

            return candidate != null ? NormalizeKlid(candidate) : null;
        }

        /// <summary>
        /// Возвращает (или генерирует и кэширует через ToUnicodeEx) полный профиль раскладки для указанного KLID.
        /// </summary>
        public DynamicLayoutProfile GetProfile(string? klidOrAlias)
        {
            string klid = NormalizeKlid(klidOrAlias);
            return _profiles.GetOrAdd(klid, BuildDynamicProfile);
        }

        private DynamicLayoutProfile BuildDynamicProfile(string klid)
        {
            KeyboardLayoutInfo info = GetLayoutInfo(klid);
            char[] charBySlot = new char[DynamicLayoutProfile.TotalSlotCount];
            var indexByChar = new Dictionary<char, int>(DynamicLayoutProfile.TotalSlotCount);
            var keyPositions = new Dictionary<char, (double Row, double Col)>();
            var alphabetChars = new HashSet<char>();

            HashSet<IntPtr> preInstalledHkls = GetInstalledHklSet();
            IntPtr hkl = LoadKeyboardLayout(klid, KLF_NOTELLSHELL);
            bool loadedTemporarily = hkl != IntPtr.Zero && !preInstalledHkls.Contains(hkl);

            try
            {
                if (hkl != IntPtr.Zero)
                {
                    var sb = new StringBuilder(8);
                    byte[] keyState = new byte[256];

                    for (int stateIdx = 0; stateIdx < DynamicLayoutProfile.ModifierStateCount; stateIdx++)
                    {
                        Array.Clear(keyState, 0, keyState.Length);
                        bool hasShift = stateIdx == 1 || stateIdx == 3;
                        bool hasAltGr = stateIdx == 2 || stateIdx == 3;

                        if (hasShift)
                        {
                            keyState[0x10] = 0x80; // VK_SHIFT
                        }

                        if (hasAltGr)
                        {
                            keyState[0x11] = 0x80; // VK_CONTROL
                            keyState[0x12] = 0x80; // VK_MENU
                            keyState[0xA5] = 0x80; // VK_RMENU
                        }

                        int baseOffset = stateIdx * DynamicLayoutProfile.KeysPerModifierState;
                        for (int k = 0; k < PhysicalKeys.Length; k++)
                        {
                            PhysicalKeyDef keyDef = PhysicalKeys[k];
                            uint vk = MapVirtualKeyEx(keyDef.ScanCode, MAPVK_VSC_TO_VK_EX, hkl);
                            if (vk == 0)
                            {
                                vk = MapVirtualKeyEx(keyDef.ScanCode, MAPVK_VSC_TO_VK, hkl);
                            }

                            if (vk == 0)
                            {
                                continue;
                            }

                            sb.Clear();
                            int rc = ToUnicodeEx(
                                vk,
                                keyDef.ScanCode,
                                keyState,
                                sb,
                                sb.Capacity,
                                TOUNICODE_DO_NOT_CHANGE_STATE,
                                hkl);

                            if (rc == 0 || sb.Length == 0)
                            {
                                continue;
                            }

                            char ch = sb[0];
                            if (ch < 0x20 || ch == 0x7F)
                            {
                                continue;
                            }

                            int slot = baseOffset + k;
                            charBySlot[slot] = ch;
                        }
                    }
                }
            }
            finally
            {
                if (loadedTemporarily)
                {
                    UnloadKeyboardLayout(hkl);
                }
            }

            // Гарантируем ANSI-совместимость для стандартной украинской раскладки 00000422
            // (на многих версиях Windows в 00000422 скан-код 0x29 возвращает 'ё', а 0x2B на ANSI-клавиатуре используется под 'ґ')
            if (string.Equals(klid, UkrainianKlid, StringComparison.OrdinalIgnoreCase))
            {
                charBySlot[0] = '\''; // 0x29 без Shift -> апостроф
                charBySlot[DynamicLayoutProfile.KeysPerModifierState + 0] = '₴'; // 0x29 с Shift -> гривня
                charBySlot[25] = 'ґ'; // 0x2B без Shift -> ґ
                charBySlot[DynamicLayoutProfile.KeysPerModifierState + 25] = 'Ґ'; // 0x2B с Shift -> Ґ
            }

            // Строим обратную таблицу символ -> первый слот (от базовых клавиш к Shift и AltGr)
            for (int slot = 0; slot < charBySlot.Length; slot++)
            {
                char ch = charBySlot[slot];
                if (ch == '\0')
                {
                    continue;
                }

                if (!indexByChar.ContainsKey(ch))
                {
                    indexByChar[ch] = slot;
                }

                int keyIndex = slot % DynamicLayoutProfile.KeysPerModifierState;
                PhysicalKeyDef keyDef = PhysicalKeys[keyIndex];

                char lower = char.ToLowerInvariant(ch);
                if (!keyPositions.ContainsKey(ch))
                {
                    keyPositions[ch] = (keyDef.Row, keyDef.Col);
                }

                if (!keyPositions.ContainsKey(lower))
                {
                    keyPositions[lower] = (keyDef.Row, keyDef.Col);
                }

                if (char.IsLetter(lower) || lower == '\'')
                {
                    alphabetChars.Add(lower);
                }
            }

            // Поддержка типографских апострофов и legacy 'ё' для украинской раскладки
            if (string.Equals(klid, UkrainianKlid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(klid, UkrainianEnhancedKlid, StringComparison.OrdinalIgnoreCase))
            {
                indexByChar['’'] = 0;
                indexByChar['ʼ'] = 0;
                if (!indexByChar.ContainsKey('ё')) indexByChar['ё'] = 0;
                if (!indexByChar.ContainsKey('Ё')) indexByChar['Ё'] = DynamicLayoutProfile.KeysPerModifierState;
                alphabetChars.Add('\'');
            }

            // Резерв базового латинского алфавита для раскладок без латинских букв в базовых слотах
            if (alphabetChars.Count == 0)
            {
                foreach (char c in LatinAlphabetChars)
                {
                    alphabetChars.Add(c);
                }
            }

            return new DynamicLayoutProfile(klid, info, charBySlot, indexByChar, keyPositions, alphabetChars);
        }

        private List<string> GetInstalledWindowsKlidsInternal()
        {
            var result = new List<string>();
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0)
            {
                return result;
            }

            var list = new IntPtr[count];
            int actual = GetKeyboardLayoutList(count, list);
            for (int i = 0; i < actual; i++)
            {
                string? klid = ResolveKlidFromHklInternal(list[i]);
                if (!string.IsNullOrEmpty(klid) && !result.Contains(klid, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(klid);
                }
            }

            return result;
        }

        private string? ResolveKlidFromHklInternal(IntPtr hkl)
        {
            if (hkl == IntPtr.Zero)
            {
                return null;
            }

            long raw = hkl.ToInt64();
            ushort langId = (ushort)(raw & 0xFFFF);
            ushort hiWord = (ushort)((raw >> 16) & 0xFFFF);

            if ((hiWord & 0xF000) == 0xF000)
            {
                ushort layoutId = (ushort)(hiWord & 0x0FFF);
                if (_layoutIdToKlid.TryGetValue(layoutId, out string? byLayoutId))
                {
                    return byLayoutId;
                }
            }

            if (hiWord != 0)
            {
                string exact = $"0000{hiWord:X4}";
                if (_catalog.ContainsKey(exact))
                {
                    return exact;
                }
            }

            if (_primaryLangToKlid.TryGetValue(langId, out string? byPrimary))
            {
                return byPrimary;
            }

            return langId != 0 ? $"0000{langId:X4}" : null;
        }

        private static HashSet<IntPtr> GetInstalledHklSet()
        {
            var set = new HashSet<IntPtr>();
            int count = GetKeyboardLayoutList(0, null);
            if (count <= 0)
            {
                return set;
            }

            var arr = new IntPtr[count];
            int actual = GetKeyboardLayoutList(count, arr);
            for (int i = 0; i < actual; i++)
            {
                set.Add(arr[i]);
            }

            return set;
        }

        private static KeyboardLayoutInfo CreateCatalogInfo(
            string klid,
            string cultureCode,
            string displayName,
            string nativeName,
            string shortBadge)
        {
            ushort langId = 0x0409;
            if (uint.TryParse(klid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint val))
            {
                langId = (ushort)(val & 0xFFFF);
            }

            string iso = cultureCode.Split('-')[0].ToLowerInvariant();
            return new KeyboardLayoutInfo
            {
                Klid = klid.ToUpperInvariant(),
                CultureCode = cultureCode,
                IsoLanguageCode = iso,
                DisplayName = displayName,
                NativeName = nativeName,
                ShortBadge = shortBadge.ToUpperInvariant(),
                LanguageId = langId
            };
        }

        private static KeyboardLayoutInfo BuildInfoFromLanguageIdAndText(string klid, ushort langId, string? registryLayoutText)
        {
            string cultureCode = "und";
            string isoCode = "xx";
            string nativeName = registryLayoutText ?? $"Layout {klid}";
            string displayName = registryLayoutText ?? $"Layout {klid}";
            string badge = "XX";

            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(langId);
                if (!string.IsNullOrWhiteSpace(culture.Name))
                {
                    cultureCode = culture.Name;
                    isoCode = culture.TwoLetterISOLanguageName.ToLowerInvariant();
                    badge = isoCode.ToUpperInvariant();
                    nativeName = ToTitleCaseFirst(culture.NativeName);
                    displayName = !string.IsNullOrWhiteSpace(registryLayoutText)
                        ? $"{ToTitleCaseFirst(culture.NativeName)} ({registryLayoutText})"
                        : ToTitleCaseFirst(culture.NativeName);
                }
            }
            catch
            {
                // Нестандартный LCID — оставляем безопасные метаданные по умолчанию
            }

            return new KeyboardLayoutInfo
            {
                Klid = klid.ToUpperInvariant(),
                CultureCode = cultureCode,
                IsoLanguageCode = isoCode,
                DisplayName = displayName,
                NativeName = nativeName,
                ShortBadge = badge,
                LanguageId = langId
            };
        }

        private static string ToTitleCaseFirst(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        private static PhysicalKeyDef[] BuildPhysicalKeys()
        {
            // 48 стандартных скан-кодов (ряды 0..3) и их физические координаты на клавиатуре
            return new[]
            {
                // Ряд 0 (цифровой ряд: 13 клавиш, индексы 0..12)
                new PhysicalKeyDef(0x29, 0.0, 0.0),  // ` / ~ / ё
                new PhysicalKeyDef(0x02, 0.0, 1.0),  // 1
                new PhysicalKeyDef(0x03, 0.0, 2.0),  // 2
                new PhysicalKeyDef(0x04, 0.0, 3.0),  // 3
                new PhysicalKeyDef(0x05, 0.0, 4.0),  // 4
                new PhysicalKeyDef(0x06, 0.0, 5.0),  // 5
                new PhysicalKeyDef(0x07, 0.0, 6.0),  // 6
                new PhysicalKeyDef(0x08, 0.0, 7.0),  // 7
                new PhysicalKeyDef(0x09, 0.0, 8.0),  // 8
                new PhysicalKeyDef(0x0A, 0.0, 9.0),  // 9
                new PhysicalKeyDef(0x0B, 0.0, 10.0), // 0
                new PhysicalKeyDef(0x0C, 0.0, 11.0), // -
                new PhysicalKeyDef(0x0D, 0.0, 12.0), // =

                // Ряд 1 (верхний буквенный ряд: 13 клавиш, индексы 13..25)
                new PhysicalKeyDef(0x10, 1.0, 1.25),  // Q / Й
                new PhysicalKeyDef(0x11, 1.0, 2.25),  // W / Ц / Z
                new PhysicalKeyDef(0x12, 1.0, 3.25),  // E / У
                new PhysicalKeyDef(0x13, 1.0, 4.25),  // R / К
                new PhysicalKeyDef(0x14, 1.0, 5.25),  // T / Е
                new PhysicalKeyDef(0x15, 1.0, 6.25),  // Y / Н / Z(DE)
                new PhysicalKeyDef(0x16, 1.0, 7.25),  // U / Г
                new PhysicalKeyDef(0x17, 1.0, 8.25),  // I / Ш
                new PhysicalKeyDef(0x18, 1.0, 9.25),  // O / Щ
                new PhysicalKeyDef(0x19, 1.0, 10.25), // P / З
                new PhysicalKeyDef(0x1A, 1.0, 11.25), // [ / Х / Ü
                new PhysicalKeyDef(0x1B, 1.0, 12.25), // ] / Ъ / +
                new PhysicalKeyDef(0x2B, 1.0, 13.25), // \ / # / ґ

                // Ряд 2 (средний буквенный ряд: 11 клавиш, индексы 26..36)
                new PhysicalKeyDef(0x1E, 2.0, 1.50),  // A / Ф / Q(FR)
                new PhysicalKeyDef(0x1F, 2.0, 2.50),  // S / Ы / І
                new PhysicalKeyDef(0x20, 2.0, 3.50),  // D / В
                new PhysicalKeyDef(0x21, 2.0, 4.50),  // F / А
                new PhysicalKeyDef(0x22, 2.0, 5.50),  // G / П
                new PhysicalKeyDef(0x23, 2.0, 6.50),  // H / Р
                new PhysicalKeyDef(0x24, 2.0, 7.50),  // J / О
                new PhysicalKeyDef(0x25, 2.0, 8.50),  // K / Л
                new PhysicalKeyDef(0x26, 2.0, 9.50),  // L / Д
                new PhysicalKeyDef(0x27, 2.0, 10.50), // ; / Ж / Ö
                new PhysicalKeyDef(0x28, 2.0, 11.50), // ' / Э / Ä

                // Ряд 3 (нижний буквенный ряд: 11 клавиш, индексы 37..47)
                new PhysicalKeyDef(0x2C, 3.0, 2.00),  // Z / Я / Y(DE) / W(FR)
                new PhysicalKeyDef(0x2D, 3.0, 3.00),  // X / Ч
                new PhysicalKeyDef(0x2E, 3.0, 4.00),  // C / С
                new PhysicalKeyDef(0x2F, 3.0, 5.00),  // V / М
                new PhysicalKeyDef(0x30, 3.0, 6.00),  // B / И
                new PhysicalKeyDef(0x31, 3.0, 7.00),  // N / Т
                new PhysicalKeyDef(0x32, 3.0, 8.00),  // M / Ь
                new PhysicalKeyDef(0x33, 3.0, 9.00),  // , / Б
                new PhysicalKeyDef(0x34, 3.0, 10.00), // . / Ю
                new PhysicalKeyDef(0x35, 3.0, 11.00), // / / . / -
                new PhysicalKeyDef(0x56, 3.0, 1.00)   // ISO 102 (< > | / ґ)
            };
        }

        #region Win32 P/Invoke

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnloadKeyboardLayout(IntPtr hkl);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(
            uint wVirtKey,
            uint wScanCode,
            byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
            int cchBuff,
            uint wFlags,
            IntPtr dwhkl);

        #endregion
    }
}
