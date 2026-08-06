using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace EmbyProxyRouter.Localization
{
    /// <summary>
    /// Language of the settings page.
    /// </summary>
    /// <remarks>
    /// To add a language: drop a <c>&lt;code&gt;.json</c> file into this folder (it is picked up as an
    /// embedded resource automatically) and add a matching entry here. Nothing else needs to change.
    /// </remarks>
    public enum PluginLanguage
    {
        Auto = 0,
        English = 1,
        Deutsch = 2
    }

    /// <summary>
    /// Resolves user-visible strings from the embedded per-language JSON files.
    /// </summary>
    /// <remarks>
    /// Kept static and free of Emby dependencies because <see cref="Strings"/> is reached through
    /// Emby's localization attributes, which call public static property getters with no context.
    ///
    /// English is the reference language: a key missing from another file falls back to en.json, and
    /// a key missing everywhere returns the key itself. A half-translated file therefore degrades to
    /// mixed language rather than to blank labels.
    /// </remarks>
    public static class Localizer
    {
        private const string ResourcePrefix = "EmbyProxyRouter.Localization.";
        private const string FallbackCode = "en";

        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Tables =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private static volatile string _code = FallbackCode;

        /// <summary>The language code currently in effect, e.g. "en" or "de".</summary>
        public static string CurrentCode
        {
            get { return _code; }
        }

        /// <summary>Language codes for which a JSON file is embedded in this assembly.</summary>
        public static IReadOnlyList<string> AvailableCodes
        {
            get
            {
                return typeof(Localizer).Assembly.GetManifestResourceNames()
                    .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                                n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - 5))
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static void SetLanguage(PluginLanguage language)
        {
            _code = ResolveCode(language);
        }

        private static string ResolveCode(PluginLanguage language)
        {
            switch (language)
            {
                case PluginLanguage.English:
                    return "en";
                case PluginLanguage.Deutsch:
                    return "de";
                default:
                    // Auto: follow the process UI culture, but only if that language is actually
                    // shipped — otherwise the page would silently fall back key by key.
                    string culture;
                    try
                    {
                        culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                    }
                    catch (Exception)
                    {
                        return FallbackCode;
                    }

                    return Load(culture).Count > 0 ? culture : FallbackCode;
            }
        }

        /// <summary>Returns the string for <paramref name="key"/> in the active language.</summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            string value;
            if (Load(_code).TryGetValue(key, out value))
            {
                return value;
            }

            if (!string.Equals(_code, FallbackCode, StringComparison.OrdinalIgnoreCase) &&
                Load(FallbackCode).TryGetValue(key, out value))
            {
                return value;
            }

            // Returning the key makes an untranslated string obvious in the UI instead of blank.
            return key;
        }

        /// <summary>Returns the string for <paramref name="key"/> with positional arguments applied.</summary>
        public static string Format(string key, params object[] args)
        {
            var template = Get(key);
            if (args == null || args.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                // A malformed placeholder in a translation must not take down the settings page.
                return template;
            }
        }

        private static Dictionary<string, string> Load(string code)
        {
            return Tables.GetOrAdd(code ?? FallbackCode, LoadFromResource);
        }

        private static Dictionary<string, string> LoadFromResource(string code)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                var assembly = typeof(Localizer).Assembly;
                using (var stream = assembly.GetManifestResourceStream(ResourcePrefix + code + ".json"))
                {
                    if (stream == null)
                    {
                        return result;
                    }

                    using (var document = JsonDocument.Parse(stream))
                    {
                        foreach (var property in document.RootElement.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                result[property.Name] = property.Value.GetString();
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A broken translation file degrades to the fallback language rather than throwing
                // during plugin construction, which would remove the plugin from the dashboard.
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return result;
        }
    }
}
