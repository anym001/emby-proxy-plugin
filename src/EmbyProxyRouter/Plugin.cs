using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using EmbyProxyRouter.Localization;
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

        /// <summary>How long saving may block waiting for a reachability verdict.</summary>
        /// <remarks>
        /// Deliberately shorter than the probe budget (a TCP timeout plus one HTTP timeout per check
        /// URL). A reachable proxy answers in well under a second, so the common case is covered,
        /// while a dead one must not hold the dashboard for the full timeout chain.
        /// </remarks>
        private static readonly TimeSpan SaveCheckBudget = TimeSpan.FromSeconds(3);

        private static readonly TimeSpan SaveCheckPoll = TimeSpan.FromMilliseconds(50);

        private readonly ILogger _logger;

        public Plugin(IApplicationHost applicationHost)
            : base(applicationHost)
        {
            Instance = this;
            _logger = applicationHost.Resolve<ILogManager>().GetLogger(Name);

            try
            {
                var options = GetOptions();

                ProxyRuntime.Initialize(_logger);
                ProxyRuntime.ApplyOptions(options);

                // Patch from the constructor rather than from the entry point. Emby creates handlers
                // lazily and then caches them per host forever, so any handler built before the patch
                // lands would keep bypassing the proxy for the lifetime of the process.
                HttpHandlerPatch.Apply(_logger, ProxyRuntime.State, ProxyRuntime.Proxy);
            }
            catch (Exception ex)
            {
                // A throwing constructor would take the whole plugin out of the dashboard, leaving
                // no way to see what went wrong.
                _logger.ErrorException("Proxy Router failed to initialise.", ex);
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
                return "Routes outbound HTTP(S) traffic from the Emby core through an HTTP, HTTPS " +
                       "or SOCKS5 proxy. Private networks are always routed directly.";
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

            // Emby renders this very object right after the save: PluginOptionsStore raises
            // OptionsSaved with the same instance that PluginOptionsPageView hands back as the new
            // view. So a status written here reaches the page without a reload — it just must not
            // say "checking", which is what applying options leaves behind until the probe lands.
            WaitForCheckResult(options);

            RefreshStatus(options);
        }

        /// <summary>
        /// Briefly blocks the save so the page can show a real verdict instead of "checking".
        /// </summary>
        /// <remarks>
        /// Polls the shared state rather than only awaiting its own call, because
        /// <see cref="ProxyRuntime.ApplyOptions"/> has already triggered a check and
        /// <c>CheckNowAsync</c> returns immediately while that one holds the gate. Either probe
        /// settles the question. Running out of budget is not an error: the periodic check fills the
        /// status in shortly afterwards, which is exactly the old behaviour.
        /// </remarks>
        private void WaitForCheckResult(PluginOptions options)
        {
            var checker = ProxyRuntime.HealthChecker;
            if (checker == null || !options.EnableProxy)
            {
                return;
            }

            try
            {
                // Task.Run keeps the blocking wait off any ambient synchronization context.
                var unused = Task.Run(() => checker.CheckNowAsync(CancellationToken.None));

                var deadline = DateTime.UtcNow + SaveCheckBudget;
                while (DateTime.UtcNow < deadline)
                {
                    if (ProxyRuntime.State.Health != ProxyHealth.Unknown)
                    {
                        return;
                    }

                    Thread.Sleep(SaveCheckPoll);
                }

                _logger.Debug("Proxy Router: no check result within the save budget; " +
                              "the page shows 'checking' until the periodic check completes.");
            }
            catch (Exception ex)
            {
                // Never let this cost the user their settings - they are already written to disk.
                _logger.ErrorException("Reachability check during save failed.", ex);
            }
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
                    Caption = Localizer.Get("PatchCaption"),
                    StatusText = Localizer.Get("PatchActive")
                };
            }
            else
            {
                options.PatchStatus = new StatusItem
                {
                    Status = ItemStatus.Failed,
                    Caption = Localizer.Get("PatchCaption"),
                    StatusText = Localizer.Format(
                        "PatchInactive",
                        HttpHandlerPatch.FailureReason ?? Localizer.Get("UnknownError"))
                };
            }

            options.FailurePolicy = options.AllowDirectWhenProxyUnavailable
                ? new StatusItem
                {
                    Status = ItemStatus.Warning,
                    Caption = Localizer.Get("FailOpenCaption"),
                    StatusText = Localizer.Get("FailOpenText")
                }
                : new StatusItem
                {
                    Status = ItemStatus.Succeeded,
                    Caption = Localizer.Get("FailClosedCaption"),
                    StatusText = Localizer.Get("FailClosedText")
                };

            if (state == null || !options.EnableProxy)
            {
                options.ProxyStatus = new StatusItem
                {
                    Status = ItemStatus.Unavailable,
                    Caption = Localizer.Get("DisabledCaption"),
                    StatusText = Localizer.Get("DisabledText")
                };
                return;
            }

            var detail = state.LastCheckDetail ?? Localizer.Get("NotCheckedYet");
            var age = state.LastCheckUtc.HasValue
                ? Localizer.Format(
                    "AgeSuffix",
                    Math.Max(0, (int)(DateTime.UtcNow - state.LastCheckUtc.Value).TotalSeconds))
                : string.Empty;

            switch (state.Health)
            {
                case ProxyHealth.Reachable:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Succeeded,
                        Caption = Localizer.Get("ReachableCaption"),
                        StatusText = detail + age
                    };
                    break;

                case ProxyHealth.Unreachable:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Failed,
                        Caption = Localizer.Get("UnreachableCaption"),
                        StatusText = detail + age + Localizer.Get(
                                         options.AllowDirectWhenProxyUnavailable
                                             ? "UnreachableSuffixFailOpen"
                                             : "UnreachableSuffixFailClosed")
                    };
                    break;

                default:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.InProgress,
                        Caption = Localizer.Get("CheckingCaption"),
                        StatusText = Localizer.Get(
                            options.AllowDirectWhenProxyUnavailable
                                ? "CheckingTextFailOpen"
                                : "CheckingTextFailClosed")
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
                _logger.Info("Proxy Router: reachability checks started, interval " +
                             ((int)ProxyRuntime.State.Settings.HealthCheckInterval.TotalSeconds)
                             .ToString(CultureInfo.InvariantCulture) + " s.");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Reachability checks could not be started.", ex);
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
