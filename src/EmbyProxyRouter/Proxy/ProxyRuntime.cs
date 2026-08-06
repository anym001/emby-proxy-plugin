using System;
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

        public static ProxyState State { get; private set; }

        public static DynamicWebProxy Proxy { get; private set; }

        public static ProxyHealthChecker HealthChecker { get; private set; }

        public static ILogger Logger { get; private set; }

        public static void Initialize(ILogger logger)
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            Logger = logger;
            State = new ProxyState();
            Proxy = new DynamicWebProxy(State);
            HealthChecker = new ProxyHealthChecker(State, logger);
        }

        /// <summary>
        /// Applies saved options and forces a fresh reachability check.
        /// </summary>
        public static void ApplyOptions(PluginOptions options)
        {
            if (State == null)
            {
                return;
            }

            var settings = ProxySettings.FromOptions(options);
            State.Apply(settings);

            if (Logger != null)
            {
                if (!settings.Enabled)
                {
                    Logger.Info("Proxy Router: deaktiviert - Emby verbindet sich direkt.");
                }
                else if (settings.Endpoint == null)
                {
                    Logger.Error("Proxy Router: aktiviert, aber Konfiguration ungültig - " +
                                 (settings.ConfigError ?? "unbekannter Fehler") +
                                 (settings.FailOpen
                                     ? " | Fail-Open: Requests gehen direkt raus."
                                     : " | Fail-Closed: betroffene Requests werden blockiert."));
                }
                else
                {
                    Logger.Info("Proxy Router: aktiviert - " + settings.Endpoint.Describe() +
                                (settings.FailOpen ? " | Fail-Open" : " | Fail-Closed") +
                                " | Prüfintervall " + (int)settings.HealthCheckInterval.TotalSeconds + " s");
                }
            }

            if (HealthChecker != null)
            {
                HealthChecker.Reschedule();
            }
        }
    }
}
