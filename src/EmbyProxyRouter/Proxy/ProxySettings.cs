using System;
using System.Collections.Generic;
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

        /// <summary>When true, an unreachable proxy falls back to a direct connection.</summary>
        public bool FailOpen { get; private set; }

        public bool IgnoreCertificateValidation { get; private set; }

        /// <summary>
        /// Every entry must answer 2xx. The list is a set of assertions, not a fallback chain.
        /// </summary>
        /// <remarks>
        /// One plain-HTTP and one HTTPS entry together prove that the proxy both forwards and
        /// tunnels. A first-success-wins pass could not establish that: it would stop at the first
        /// entry and never reach the second.
        /// </remarks>
        public IReadOnlyList<string> HealthCheckUrls { get; private set; }

        public TimeSpan HealthCheckInterval { get; private set; }

        /// <summary>
        /// Entries this snapshot had to discard, already rendered in English for the log.
        /// </summary>
        /// <remarks>
        /// Not a <see cref="LocalizedText"/> like <see cref="ConfigError"/>, because it has one
        /// audience rather than two: nothing on the settings page shows these. The page cannot
        /// produce them in the first place — <c>Validate</c> rejects a bad check URL before it is
        /// ever saved — so the only way to get one is by editing the options JSON by hand, and the
        /// only place that is worth reporting is the log.
        ///
        /// Reported rather than dropped quietly, because a discarded check URL weakens exactly the
        /// verification the two fields exist to guarantee, and this plugin does not fail silently.
        /// </remarks>
        public IReadOnlyList<string> ConfigWarnings { get; private set; }

        public static ProxySettings Disabled()
        {
            return new ProxySettings
            {
                Enabled = false,
                Bypass = BypassRules.Parse(null),
                HealthCheckUrls = new string[0],
                ConfigWarnings = new string[0],
                HealthCheckInterval = TimeSpan.FromSeconds(60)
            };
        }

        public static ProxySettings FromOptions(PluginOptions options)
        {
            if (options == null)
            {
                return Disabled();
            }

            // HTTP first: it is the cheaper probe, and a proxy that refuses to forward at all should
            // not cost a TLS handshake before the verdict is in.
            var urls = new List<string>();
            var warnings = new List<string>();
            AddCheckUrl(urls, warnings, options.HealthCheckUrlHttp, "http");
            AddCheckUrl(urls, warnings, options.HealthCheckUrlHttps, "https");

            // Clamped at both ends. Validate rejects an out-of-range value entered on the page, but
            // the options file can be edited directly and is read back without going through it, so
            // the bound the UI advertises has to be enforced here as well or it is only a label.
            var interval = options.HealthCheckIntervalSeconds;
            if (interval < PluginOptions.MinCheckIntervalSeconds)
            {
                interval = PluginOptions.MinCheckIntervalSeconds;
            }
            else if (interval > PluginOptions.MaxCheckIntervalSeconds)
            {
                interval = PluginOptions.MaxCheckIntervalSeconds;
            }

            var settings = new ProxySettings
            {
                Enabled = options.EnableProxy,
                Bypass = BypassRules.Parse(options.BypassList),
                FailOpen = options.AllowDirectWhenProxyUnavailable,
                IgnoreCertificateValidation = options.IgnoreCertificateValidation,
                HealthCheckUrls = urls,
                ConfigWarnings = warnings,
                HealthCheckInterval = TimeSpan.FromSeconds(interval)
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

        /// <summary>
        /// Takes one check URL if it can do the job its field claims, and says so when it cannot.
        /// </summary>
        /// <remarks>
        /// An empty field means "skip this probe", not "check an empty URL".
        ///
        /// The scheme is enforced here and not only in <c>PluginOptions.Validate</c>, for the same
        /// reason the interval is clamped above: the options file is read back without going through
        /// Validate, so a bound that only the page enforces is a label. It matters more here than it
        /// does for the interval. The two fields are split by scheme precisely so that one HTTP and
        /// one HTTPS probe together prove the proxy both forwards and tunnels, and an https:// URL
        /// sitting in the HTTP field would probe the CONNECT tunnel twice and report a healthy proxy
        /// whose forwarding path was never exercised — the exact blind spot the split exists to
        /// close, reopened without a word.
        ///
        /// A URL that fails is dropped rather than kept, so it cannot masquerade as covering a path
        /// it does not, and the reason is recorded for the log so the drop is not silent.
        /// </remarks>
        private static void AddCheckUrl(
            List<string> urls, List<string> warnings, string url, string requiredScheme)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var trimmed = url.Trim();

            Uri parsed;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out parsed))
            {
                warnings.Add(Localizer.FormatInvariant("LogCheckUrlInvalid", trimmed, requiredScheme));
                return;
            }

            if (!string.Equals(parsed.Scheme, requiredScheme, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(Localizer.FormatInvariant(
                    "LogCheckUrlScheme", trimmed, requiredScheme, parsed.Scheme));
                return;
            }

            urls.Add(trimmed);
        }
    }
}
