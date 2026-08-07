namespace EmbyProxyRouter.Localization
{
    /// <summary>
    /// Resource surface for Emby's localization attributes.
    /// </summary>
    /// <remarks>
    /// <c>[DisplayNameL(nameof(Strings.X), typeof(Strings))]</c> resolves through
    /// MediaBrowser.Model's LocalizableString, which requires the resource type to be a public class
    /// exposing a public static string property with a getter. It caches the reflected PropertyInfo
    /// but re-invokes the getter on every read, so routing each property through
    /// <see cref="Localizer"/> makes the settings page follow a language change without a restart.
    ///
    /// Only strings referenced from attributes need a property here. Everything else calls
    /// <see cref="Localizer.Get"/> or <see cref="Localizer.Format"/> directly.
    /// </remarks>
    public static class Strings
    {
        public static string LabelProxyStatus => Localizer.Get(nameof(LabelProxyStatus));
        public static string LabelFailurePolicy => Localizer.Get(nameof(LabelFailurePolicy));
        public static string LabelPatchStatus => Localizer.Get(nameof(LabelPatchStatus));

        public static string LabelEnableProxy => Localizer.Get(nameof(LabelEnableProxy));
        public static string DescEnableProxy => Localizer.Get(nameof(DescEnableProxy));

        public static string LabelScheme => Localizer.Get(nameof(LabelScheme));
        public static string DescScheme => Localizer.Get(nameof(DescScheme));

        public static string LabelProxyAddress => Localizer.Get(nameof(LabelProxyAddress));
        public static string DescProxyAddress => Localizer.Get(nameof(DescProxyAddress));

        public static string LabelUsername => Localizer.Get(nameof(LabelUsername));
        public static string DescUsername => Localizer.Get(nameof(DescUsername));

        public static string LabelPassword => Localizer.Get(nameof(LabelPassword));

        public static string LabelIgnoreCert => Localizer.Get(nameof(LabelIgnoreCert));
        public static string DescIgnoreCert => Localizer.Get(nameof(DescIgnoreCert));

        public static string LabelFailOpen => Localizer.Get(nameof(LabelFailOpen));
        public static string DescFailOpen => Localizer.Get(nameof(DescFailOpen));

        public static string LabelBypassPrivate => Localizer.Get(nameof(LabelBypassPrivate));
        public static string DescBypassPrivate => Localizer.Get(nameof(DescBypassPrivate));

        public static string LabelBypassList => Localizer.Get(nameof(LabelBypassList));
        public static string DescBypassList => Localizer.Get(nameof(DescBypassList));

        public static string LabelCheckUrlHttp => Localizer.Get(nameof(LabelCheckUrlHttp));
        public static string DescCheckUrlHttp => Localizer.Get(nameof(DescCheckUrlHttp));

        public static string LabelCheckUrlHttps => Localizer.Get(nameof(LabelCheckUrlHttps));
        public static string DescCheckUrlHttps => Localizer.Get(nameof(DescCheckUrlHttps));

        public static string LabelCheckInterval => Localizer.Get(nameof(LabelCheckInterval));

        /// <summary>
        /// The only description here that is formatted rather than looked up: it quotes the bounds,
        /// and those come from <see cref="PluginOptions"/> so the page cannot advertise a range the
        /// validation does not enforce. Both are compile-time constants, so this pulls in no type.
        /// </summary>
        public static string DescCheckInterval => Localizer.Format(
            nameof(DescCheckInterval),
            PluginOptions.MinCheckIntervalSeconds,
            PluginOptions.MaxCheckIntervalSeconds);
    }
}
