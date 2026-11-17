using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LanguageSwitcher.Models;

namespace LanguageSwitcher.Services
{
    /// <summary>
    /// Управляет таблицей пользовательских автозамен (autoreplace.json) и выполняет Приоритет 1 конвейера
    /// с поддержкой умного регистра и многострочных шаблонов.
    /// </summary>
    public sealed class AutoReplaceService
    {
        private readonly SettingsManager _settingsManager;
        private readonly object _lock = new object();
        private List<AutoReplaceRule> _rules = new List<AutoReplaceRule>();
        private Dictionary<string, AutoReplaceRule> _activeRuleMap =
            new Dictionary<string, AutoReplaceRule>(StringComparer.OrdinalIgnoreCase);

        public AutoReplaceService(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            ReloadFromDisk();
        }

        public void ReloadFromDisk()
        {
            List<AutoReplaceRule> loaded = _settingsManager.LoadAutoReplaceRules();
            lock (_lock)
            {
                ApplyRulesLocked(loaded);
            }
        }

        public List<AutoReplaceRule> GetRulesSnapshot()
        {
            lock (_lock)
            {
                return _rules
                    .Select(r => new AutoReplaceRule
                    {
                        Trigger = r.Trigger,
                        Replacement = r.Replacement,
                        IsEnabled = r.IsEnabled
                    })
                    .ToList();
            }
        }

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _rules.Count;
                }
            }
        }

        public bool AddOrUpdateRule(string trigger, string replacement, bool isEnabled = true, string? originalTrigger = null)
        {
            string normalizedTrigger = NormalizeTrigger(trigger);
            if (string.IsNullOrEmpty(normalizedTrigger) || replacement == null)
            {
                return false;
            }

            string normalizedReplacement = NormalizeReplacement(replacement);

            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(originalTrigger))
                {
                    string prev = NormalizeTrigger(originalTrigger);
                    _rules.RemoveAll(r => string.Equals(r.Trigger, prev, StringComparison.OrdinalIgnoreCase));
                }

                AutoReplaceRule? existing = _rules.FirstOrDefault(r =>
                    string.Equals(r.Trigger, normalizedTrigger, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    existing.Trigger = normalizedTrigger;
                    existing.Replacement = normalizedReplacement;
                    existing.IsEnabled = isEnabled;
                }
                else
                {
                    _rules.Add(new AutoReplaceRule
                    {
                        Trigger = normalizedTrigger,
                        Replacement = normalizedReplacement,
                        IsEnabled = isEnabled
                    });
                }

                RebuildMapAndSaveLocked();
                return true;
            }
        }

        public void SetRuleEnabled(string trigger, bool isEnabled)
        {
            string normalizedTrigger = NormalizeTrigger(trigger);
            lock (_lock)
            {
                AutoReplaceRule? rule = _rules.FirstOrDefault(r =>
                    string.Equals(r.Trigger, normalizedTrigger, StringComparison.OrdinalIgnoreCase));

                if (rule != null && rule.IsEnabled != isEnabled)
                {
                    rule.IsEnabled = isEnabled;
                    RebuildMapAndSaveLocked();
                }
            }
        }

        public bool RemoveRule(string trigger)
        {
            string normalizedTrigger = NormalizeTrigger(trigger);
            lock (_lock)
            {
                int removed = _rules.RemoveAll(r =>
                    string.Equals(r.Trigger, normalizedTrigger, StringComparison.OrdinalIgnoreCase));

                if (removed > 0)
                {
                    RebuildMapAndSaveLocked();
                    return true;
                }
            }

            return false;
        }

        public void ReplaceAllRules(IEnumerable<AutoReplaceRule> rules)
        {
            lock (_lock)
            {
                ApplyRulesLocked(rules);
                _settingsManager.SaveAutoReplaceRules(_rules);
            }
        }

        public void ClearRules()
        {
            lock (_lock)
            {
                _rules.Clear();
                _activeRuleMap.Clear();
                _settingsManager.SaveAutoReplaceRules(_rules);
            }
        }

        /// <summary>
        /// Приоритет 1: проверяет, совпадает ли введённое слово с активным правилом автозамены,
        /// и возвращает текст с учётом умного регистра.
        /// </summary>
        public bool TryResolveReplacement(string typedWord, AppSettings settings, out string replacementText)
        {
            replacementText = string.Empty;
            if (settings == null || !settings.EnableCustomAutoReplace || string.IsNullOrWhiteSpace(typedWord))
            {
                return false;
            }

            AutoReplaceRule? rule;
            lock (_lock)
            {
                _activeRuleMap.TryGetValue(typedWord.Trim(), out rule);
            }

            if (rule == null)
            {
                return false;
            }

            replacementText = ApplySmartCase(typedWord, rule.Replacement);
            return true;
        }

        /// <summary>
        /// Если Replacement задан в нижнем регистре, адаптирует регистр под ввод (спс -> спасибо, Спс -> Спасибо, СПС -> СПАСИБО).
        /// Если в Replacement смешанный регистр — возвращает текст точно как в правиле.
        /// </summary>
        public static string ApplySmartCase(string typedTrigger, string ruleReplacement)
        {
            if (string.IsNullOrEmpty(ruleReplacement) || string.IsNullOrEmpty(typedTrigger))
            {
                return ruleReplacement ?? string.Empty;
            }

            // Если правило содержит хотя бы одну заглавную букву — вставляем дословно (имена, бренды, адреса)
            for (int i = 0; i < ruleReplacement.Length; i++)
            {
                if (char.IsUpper(ruleReplacement[i]))
                {
                    return ruleReplacement;
                }
            }

            int letterCount = 0;
            int upperCount = 0;
            bool firstLetterUpper = false;

            for (int i = 0; i < typedTrigger.Length; i++)
            {
                char ch = typedTrigger[i];
                if (!char.IsLetter(ch))
                {
                    continue;
                }

                if (letterCount == 0)
                {
                    firstLetterUpper = char.IsUpper(ch);
                }

                letterCount++;
                if (char.IsUpper(ch))
                {
                    upperCount++;
                }
            }

            if (letterCount == 0 || upperCount == 0)
            {
                return ruleReplacement;
            }

            // Весь ввод в верхнем регистре (СПС -> СПАСИБО)
            if (letterCount >= 2 && upperCount == letterCount)
            {
                return ruleReplacement.ToUpper(CultureInfo.CurrentCulture);
            }

            // Первая буква заглавная (Спс -> Спасибо)
            if (firstLetterUpper)
            {
                char[] chars = ruleReplacement.ToCharArray();
                for (int i = 0; i < chars.Length; i++)
                {
                    if (char.IsLetter(chars[i]))
                    {
                        chars[i] = char.ToUpper(chars[i], CultureInfo.CurrentCulture);
                        break;
                    }
                }

                return new string(chars);
            }

            return ruleReplacement;
        }

        private void ApplyRulesLocked(IEnumerable<AutoReplaceRule> rules)
        {
            _rules = new List<AutoReplaceRule>();
            _activeRuleMap = new Dictionary<string, AutoReplaceRule>(StringComparer.OrdinalIgnoreCase);

            foreach (AutoReplaceRule rule in rules)
            {
                if (rule == null)
                {
                    continue;
                }

                string trigger = NormalizeTrigger(rule.Trigger);
                if (string.IsNullOrEmpty(trigger))
                {
                    continue;
                }

                var cleaned = new AutoReplaceRule
                {
                    Trigger = trigger,
                    Replacement = NormalizeReplacement(rule.Replacement),
                    IsEnabled = rule.IsEnabled
                };

                _rules.RemoveAll(r => string.Equals(r.Trigger, trigger, StringComparison.OrdinalIgnoreCase));
                _rules.Add(cleaned);
            }

            RebuildActiveMapLocked();
        }

        private void RebuildMapAndSaveLocked()
        {
            RebuildActiveMapLocked();
            _settingsManager.SaveAutoReplaceRules(_rules);
        }

        private void RebuildActiveMapLocked()
        {
            _activeRuleMap.Clear();
            foreach (AutoReplaceRule rule in _rules)
            {
                if (rule.IsEnabled)
                {
                    _activeRuleMap[rule.Trigger] = rule;
                }
            }
        }

        private static string NormalizeTrigger(string? trigger) =>
            string.IsNullOrWhiteSpace(trigger) ? string.Empty : trigger.Trim();

        private static string NormalizeReplacement(string? replacement) =>
            (replacement ?? string.Empty).Replace("\r\n", "\n");
    }
}
