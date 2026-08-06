using System;
using System.Globalization;
using Emby.Web.GenericEdit.Elements;
using EmbyProxyRouter.Patch;
using EmbyProxyRouter.Proxy;
using MediaBrowser.Common;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter
{
    /// <summary>
    /// Routes Emby's own outbound HTTP(S) traffic through a configurable HTTP/HTTPS/SOCKS5 proxy.
    /// </summary>
    /// <remarks>
    /// Scope is deliberately one thing. No metadata handling, no subtitle logic, no auto-update.
    /// </remarks>
    public class Plugin : BasePluginSimpleUI<PluginOptions>
    {
        private static readonly Guid PluginId = new Guid("5f1c1b6e-9a3d-4d21-8f0a-2b7c6e4d91a3");

        private readonly ILogger _logger;

        public Plugin(IApplicationHost applicationHost)
            : base(applicationHost)
        {
            Instance = this;
            _logger = applicationHost.Resolve<ILogManager>().GetLogger(Name);

            try
            {
                ProxyRuntime.Initialize(_logger);
                ProxyRuntime.ApplyOptions(GetOptions());

                // Patch from the constructor rather than from the entry point. Emby creates handlers
                // lazily and then caches them per host forever, so any handler built before the patch
                // lands would keep bypassing the proxy for the lifetime of the process.
                HttpHandlerPatch.Apply(_logger, ProxyRuntime.State, ProxyRuntime.Proxy);
            }
            catch (Exception ex)
            {
                // A throwing constructor would take the whole plugin out of the dashboard, leaving
                // no way to see what went wrong.
                _logger.ErrorException("Proxy Router konnte nicht initialisiert werden.", ex);
            }
        }

        public static Plugin Instance { get; private set; }

        public override string Name
        {
            get { return "Proxy Router"; }
        }

        public override string Description
        {
            get
            {
                return "Leitet ausgehenden HTTP(S)-Traffic des Emby-Kerns über einen HTTP-, HTTPS- " +
                       "oder SOCKS5-Proxy. Private Netze werden immer direkt geroutet.";
            }
        }

        public override Guid Id
        {
            get { return PluginId; }
        }

        internal PluginOptions CurrentOptions
        {
            get { return GetOptions(); }
        }

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            RefreshStatus(options);

            // Kick off a check in the background so the next page load is current, without making
            // the dashboard wait on network I/O.
            if (ProxyRuntime.HealthChecker != null && options.EnableProxy)
            {
                ProxyRuntime.HealthChecker.Reschedule();
            }

            return options;
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            ProxyRuntime.ApplyOptions(options);
            RefreshStatus(options);
        }

        /// <summary>
        /// Fills the three read-only status lines shown at the top of the settings page.
        /// </summary>
        private void RefreshStatus(PluginOptions options)
        {
            var state = ProxyRuntime.State;

            // Patch status first: without the patch nothing else on this page has any effect, and
            // saying so plainly beats letting the user believe a green proxy means traffic is routed.
            if (HttpHandlerPatch.IsApplied)
            {
                options.PatchStatus = new StatusItem
                {
                    Status = ItemStatus.Succeeded,
                    Caption = "Patch",
                    StatusText = "Aktiv - Emby-Kern-Traffic läuft über dieses Plugin."
                };
            }
            else
            {
                options.PatchStatus = new StatusItem
                {
                    Status = ItemStatus.Failed,
                    Caption = "Patch",
                    StatusText = "NICHT aktiv - " + (HttpHandlerPatch.FailureReason ?? "unbekannter Fehler") +
                                 " Es wird kein Traffic umgeleitet."
                };
            }

            options.FailurePolicy = options.AllowDirectWhenProxyUnavailable
                ? new StatusItem
                {
                    Status = ItemStatus.Warning,
                    Caption = "Fail-Open",
                    StatusText = "Bei Proxy-Ausfall gehen Requests OHNE Proxy direkt ins Internet " +
                                 "(wird als Warnung geloggt)."
                }
                : new StatusItem
                {
                    Status = ItemStatus.Succeeded,
                    Caption = "Fail-Closed",
                    StatusText = "Bei Proxy-Ausfall werden betroffene Requests blockiert. " +
                                 "Kein stiller Rückfall auf Direktverbindung."
                };

            if (state == null || !options.EnableProxy)
            {
                options.ProxyStatus = new StatusItem
                {
                    Status = ItemStatus.Unavailable,
                    Caption = "Deaktiviert",
                    StatusText = "Der Proxy ist ausgeschaltet; Emby verbindet sich direkt."
                };
                return;
            }

            var detail = state.LastCheckDetail ?? "Noch nicht geprüft";
            var age = state.LastCheckUtc.HasValue
                ? " (Stand: vor " +
                  Math.Max(0, (int)(DateTime.UtcNow - state.LastCheckUtc.Value).TotalSeconds) + " s)"
                : string.Empty;

            switch (state.Health)
            {
                case ProxyHealth.Reachable:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Succeeded,
                        Caption = "Erreichbar",
                        StatusText = detail + age
                    };
                    break;

                case ProxyHealth.Unreachable:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Failed,
                        Caption = "Nicht erreichbar",
                        StatusText = detail + age +
                                     (options.AllowDirectWhenProxyUnavailable
                                         ? " - Requests gehen aktuell direkt raus."
                                         : " - Requests werden aktuell blockiert.")
                    };
                    break;

                default:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.InProgress,
                        Caption = "Wird geprüft",
                        StatusText = "Noch kein Prüfergebnis." +
                                     (options.AllowDirectWhenProxyUnavailable
                                         ? " Bis dahin gehen Requests direkt raus."
                                         : " Bis dahin werden betroffene Requests blockiert.")
                    };
                    break;
            }
        }
    }

    /// <summary>
    /// Starts the periodic reachability check once the server is up.
    /// </summary>
    /// <remarks>
    /// Separate from the plugin constructor on purpose: the constructor runs during plugin discovery
    /// and must stay free of network I/O, but the Harmony patch must be applied there. Splitting the
    /// two keeps startup fast without letting handlers get created before the patch is in place.
    /// </remarks>
    public sealed class ProxyRouterEntryPoint : IServerEntryPoint
    {
        private readonly ILogger _logger;

        public ProxyRouterEntryPoint(ILogManager logManager)
        {
            _logger = logManager.GetLogger("Proxy Router");
        }

        public void Run()
        {
            try
            {
                if (ProxyRuntime.HealthChecker == null)
                {
                    return;
                }

                ProxyRuntime.HealthChecker.Start();
                _logger.Info("Proxy Router: Erreichbarkeitsprüfung gestartet, Intervall " +
                             ((int)ProxyRuntime.State.Settings.HealthCheckInterval.TotalSeconds)
                             .ToString(CultureInfo.InvariantCulture) + " s.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Erreichbarkeitsprüfung konnte nicht gestartet werden.", ex);
            }
        }

        public void Dispose()
        {
            if (ProxyRuntime.HealthChecker != null)
            {
                ProxyRuntime.HealthChecker.Dispose();
            }
        }
    }
}
