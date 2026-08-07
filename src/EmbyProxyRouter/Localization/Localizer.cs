using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace EmbyProxyRouter.Localization
{
    /// <summary>
    /// Resolves user-visible strings from the embedded per-language JSON files.
    /// </summary>
    /// <remarks>
    /// The language is not a plugin setting. It follows the server's display language, which Emby
    /// applies process-wide in <c>ApplicationHost.SetDefaultThreadCulture</c>:
    /// <code>
    /// CultureInfo.DefaultThreadCurrentUICulture = (CultureInfo.CurrentUICulture = cultureInfo);
    /// </code>
    /// That runs both in the host constructor and again from <c>OnConfigurationUpdated</c>, and the
    /// server installs no per-request localization middleware. Reading
    /// <see cref="CultureInfo.CurrentUICulture"/> at lookup time therefore tracks the dashboard
    /// language exactly, and picks up a change to it without a restart.
    ///
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

        private static readonly Lazy<string[]> Codes = new Lazy<string[]>(DiscoverCodes);

        private static readonly ConcurrentDictionary<string, Dictionary<string, string>> Tables =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // Culture name -> shipped language code. Resolution is a few string comparisons, but it runs
        // on every label read while the settings page renders, so the answer is memoised.
        private static readonly ConcurrentDictionary<string, string> ResolvedCodes =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The language code currently in effect, derived from Emby's display language.
        /// </summary>
        public static string CurrentCode
        {
            get
            {
                string cultureName;
                try
                {
                    cultureName = CultureInfo.CurrentUICulture.Name;
                }
                catch (Exception)
                {
                    return FallbackCode;
                }

                if (string.IsNullOrEmpty(cultureName))
                {
                    // The invariant culture carries no language information.
                    return FallbackCode;
                }

                return ResolvedCodes.GetOrAdd(cultureName, ResolveCode);
            }
        }

        /// <summary>Returns the string for <paramref name="key"/> in the active language.</summary>
        public static string Get(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            var code = CurrentCode;

            string value;
            if (Load(code).TryGetValue(key, out value))
            {
                return value;
            }

            if (!string.Equals(code, FallbackCode, StringComparison.OrdinalIgnoreCase) &&
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

        /// <summary>
        /// Maps an Emby culture name onto one of the shipped language files.
        /// </summary>
        /// <remarks>
        /// Emby's language values are a mix of neutral and regional codes ("de", "pt-BR", "zh-CN",
        /// and "en-us" by default), so an exact match is tried before falling back to the neutral
        /// part. That lets a regional translation be added later as its own file without changing
        /// anything here: pt-BR.json wins for pt-BR, and pt.json still covers pt-PT.
        /// </remarks>
        private static string ResolveCode(string cultureName)
        {
            var codes = Codes.Value;

            foreach (var code in codes)
            {
                if (string.Equals(code, cultureName, StringComparison.OrdinalIgnoreCase))
                {
                    return code;
                }
            }

            var separator = cultureName.IndexOf('-');
            if (separator > 0)
            {
                var neutral = cultureName.Substring(0, separator);
                foreach (var code in codes)
                {
                    if (string.Equals(code, neutral, StringComparison.OrdinalIgnoreCase))
                    {
                        return code;
                    }
                }
            }

            return FallbackCode;
        }

        private static string[] DiscoverCodes()
        {
            try
            {
                return typeof(Localizer).Assembly.GetManifestResourceNames()
                    .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                                n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - 5))
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception)
            {
                return new[] { FallbackCode };
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
