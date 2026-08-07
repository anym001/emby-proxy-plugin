using System;

namespace EmbyProxyRouter.Localization
{
    /// <summary>
    /// A message that has not been rendered yet, so it can be rendered per destination.
    /// </summary>
    /// <remarks>
    /// A few values in this plugin genuinely reach two audiences. The reachability detail is shown
    /// on the settings page *and* written to the Emby log; so is the parse error for a bad proxy
    /// address, and the endpoint description embedded in both. The page should be in the user's
    /// language and the log should be in English, and a value rendered at the point it is produced
    /// can only be one of those.
    ///
    /// So this carries the key and its arguments instead of a string, and the two sinks each ask
    /// for what they need: <see cref="Localized"/> for the dashboard,
    /// <see cref="Invariant"/> for the log. Deferring also means the settings page follows a
    /// language change without waiting for the next check to overwrite a detail rendered in the
    /// previous language — the same property <see cref="Strings"/> relies on.
    ///
    /// <see cref="ToString"/> deliberately returns the English form. Accidental interpolation of one
    /// of these into a string almost always happens in a log statement, and English in the UI
    /// degrades to the fallback language the plugin already ships; a translated log line is the
    /// worse of the two failures.
    /// </remarks>
    public sealed class LocalizedText
    {
        private readonly string _key;
        private readonly object[] _args;

        private LocalizedText(string key, object[] args)
        {
            _key = key;
            _args = args;
        }

        public static LocalizedText Of(string key, params object[] args)
        {
            return new LocalizedText(key, args);
        }

        /// <summary>The resource key, for tests and diagnostics.</summary>
        public string Key
        {
            get { return _key; }
        }

        /// <summary>Rendered in the dashboard language. For anything the user reads on the page.</summary>
        public string Localized()
        {
            return Localizer.Format(_key, Render(_args, localized: true));
        }

        /// <summary>Rendered in English. For anything written to the Emby log.</summary>
        public string Invariant()
        {
            return Localizer.FormatInvariant(_key, Render(_args, localized: false));
        }

        /// <summary>
        /// Renders nested messages in the same language as the message embedding them.
        /// </summary>
        /// <remarks>
        /// These compose: a reachability detail quotes the failing probe's error, and quotes the
        /// endpoint description. Left to <c>string.Format</c> the nested value would go through
        /// <see cref="ToString"/> and come out English, so a German page would show a German
        /// sentence with an English clause inside it. Mapping the arguments first keeps a rendered
        /// message in one language throughout.
        /// </remarks>
        private static object[] Render(object[] args, bool localized)
        {
            if (args == null || args.Length == 0)
            {
                return args;
            }

            object[] rendered = null;

            for (var i = 0; i < args.Length; i++)
            {
                var nested = args[i] as LocalizedText;
                if (nested == null)
                {
                    continue;
                }

                if (rendered == null)
                {
                    rendered = (object[])args.Clone();
                }

                rendered[i] = localized ? nested.Localized() : nested.Invariant();
            }

            return rendered ?? args;
        }

        public override string ToString()
        {
            return Invariant();
        }
    }
}
