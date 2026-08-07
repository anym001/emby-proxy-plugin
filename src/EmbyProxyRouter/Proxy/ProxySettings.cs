using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// An immutable snapshot of the configuration, built once whenever options are saved.
    /// </summary>
    /// <remarks>
    /// Snapshotting matters because <see cref="DynamicWebProxy"/> is consulted on every single
    /// request, from arbitrary threads. Reading a coherent, never-mutated object avoids both locking
    /// and the possibility of a request seeing a half-applied configuration.
    /// </remarks>
    public sealed class ProxySettings
    {
        public bool Enabled { get; private set; }

        /// <summary>Null when the proxy is disabled or the address does not parse.</summary>
        public ProxyEndpoint Endpoint { get; private set; }

        /// <summary>Non-null when the configured address could not be parsed.</summary>
        /// <remarks>
        /// Deferred rather than rendered: it is reported on the settings page in the dashboard
        /// language and written to the log in English.
        /// </remarks>
        public LocalizedText ConfigError { get; private set; }

        public BypassRules Bypass { get; private set; }

        public bool IgnoreCertificateValidation { get; private set; }

        public static ProxySettings Disabled()
        {
            return new ProxySettings
            {
                Enabled = false,

                // Matches the option's default so there is one answer to "what does an
                // unconfigured plugin bypass". Academic either way: Enabled is false, so Decide
                // returns Direct for every destination without consulting these rules at all.
                Bypass = BypassRules.Parse(null, false)
            };
        }

        public static ProxySettings FromOptions(PluginOptions options)
        {
            if (options == null)
            {
                return Disabled();
            }

            var settings = new ProxySettings
            {
                Enabled = options.EnableProxy,
                Bypass = BypassRules.Parse(options.BypassList, options.BypassPrivateNetworks),
                IgnoreCertificateValidation = options.IgnoreCertificateValidation
            };

            ProxyEndpoint endpoint;
            LocalizedText error;
            if (ProxyEndpoint.TryParse(
                    options.ProxyAddress, options.Scheme, options.Username, options.Password,
                    out endpoint, out error))
            {
                settings.Endpoint = endpoint;
            }
            else if (options.EnableProxy)
            {
                settings.ConfigError = error;
            }

            return settings;
        }
    }
}
