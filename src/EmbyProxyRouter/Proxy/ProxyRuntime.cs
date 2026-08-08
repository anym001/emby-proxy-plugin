using System.Threading;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Holds the objects that must outlive any single request or settings change.
    /// </summary>
    /// <remarks>
    /// A static holder is warranted here because the Harmony postfix is necessarily a static method:
    /// it has no instance to reach the plugin through.
    /// </remarks>
    public static class ProxyRuntime
    {
        private static int _initialized;
        private static ILogger _logger;

        public static ProxyState State { get; private set; }

        public static DynamicWebProxy Proxy { get; private set; }

        public static void Initialize(ILogger logger)
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            _logger = logger;
            State = new ProxyState();
            Proxy = new DynamicWebProxy(State);
        }

        /// <summary>
        /// Applies saved options and states the resulting policy in the log.
        /// </summary>
        public static void ApplyOptions(PluginOptions options)
        {
            if (State == null)
            {
                return;
            }

            var settings = ProxySettings.FromOptions(options);
            State.Apply(settings);

            if (_logger == null)
            {
                return;
            }

            if (!settings.Enabled)
            {
                _logger.Info("Proxy Router: disabled - Emby connects directly.");
            }
            else if (settings.Endpoint == null)
            {
                _logger.Error("Proxy Router: enabled, but the configuration is invalid - " +
                              (settings.ConfigError != null ? settings.ConfigError.Invariant() : "unknown error") +
                              " | affected requests will be blocked.");
            }
            else
            {
                _logger.Info("Proxy Router: enabled - " + settings.Endpoint.Describe().Invariant() +
                            // Stated on every apply because it is not visible from the routing
                            // decisions themselves, and it decides whether a dead proxy also costs
                            // the server its own LAN.
                            (settings.Bypass.BypassPrivateNetworks
                                ? " | private networks bypassed"
                                : " | private networks proxied"));
            }
        }
    }
}
