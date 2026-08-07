using System;
using System.Collections.Generic;

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
        public string ConfigError { get; private set; }

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

        public static ProxySettings Disabled()
        {
            return new ProxySettings
            {
                Enabled = false,
                Bypass = BypassRules.Parse(null),
                HealthCheckUrls = new string[0],
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
            AddCheckUrl(urls, options.HealthCheckUrlHttp);
            AddCheckUrl(urls, options.HealthCheckUrlHttps);

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
                HealthCheckInterval = TimeSpan.FromSeconds(interval)
            };

            ProxyEndpoint endpoint;
            string error;
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

        /// <summary>An empty field means "skip this probe", not "check an empty URL".</summary>
        private static void AddCheckUrl(List<string> urls, string url)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                urls.Add(url.Trim());
            }
        }
    }
}
