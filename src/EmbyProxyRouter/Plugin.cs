using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Web.GenericEdit.Elements;
using EmbyProxyRouter.Localization;
using EmbyProxyRouter.Patch;
using EmbyProxyRouter.Proxy;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter
{
    /// <summary>
    /// Routes Emby's own outbound HTTP(S) traffic through a configurable HTTP/HTTPS/SOCKS5 proxy.
    /// </summary>
    /// <remarks>
    /// Scope is deliberately one thing. No metadata handling, no subtitle logic, no auto-update.
    /// </remarks>
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        private static readonly Guid PluginId = new Guid("5f1c1b6e-9a3d-4d21-8f0a-2b7c6e4d91a3");

        /// <summary>Logical name of the tile embedded by the csproj.</summary>
        private const string ThumbResource = "EmbyProxyRouter.thumb.png";

        private readonly ILogger _logger;

        public Plugin(IApplicationHost applicationHost)
            : base(applicationHost)
        {
            try
            {
                // Inside the try as well: resolving the log manager is the one step whose failure
                // would otherwise throw before there is anything to report the throw with, and a
                // throwing constructor takes the plugin out of the dashboard entirely.
                _logger = applicationHost.Resolve<ILogManager>().GetLogger(Name);

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
                if (_logger != null)
                {
                    _logger.ErrorException("Proxy Router failed to initialise.", ex);
                }
            }
        }

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

        public ImageFormat ThumbImageFormat
        {
            get { return ImageFormat.Png; }
        }

        /// <summary>
        /// The tile Emby shows for this plugin in the dashboard's plugin list.
        /// </summary>
        /// <remarks>
        /// Embedded in the DLL rather than deployed beside it, for the same reason as Harmony and
        /// the translations: installing stays a single file copy. Emby disposes the stream it gets,
        /// so this hands out a fresh one per call. A null return — the resource having gone missing
        /// from the build — simply leaves the default placeholder in place, which is what the
        /// dashboard showed before this existed.
        /// </remarks>
        public Stream GetThumbImage()
        {
            return typeof(Plugin).Assembly.GetManifestResourceStream(ThumbResource);
        }

        protected override PluginOptions OnBeforeShowUI(PluginOptions options)
        {
            RefreshStatus(options, Probe(options));
            return options;
        }

        protected override void OnOptionsSaved(PluginOptions options)
        {
            ProxyRuntime.ApplyOptions(options);

            // Emby renders this very object right after the save: PluginOptionsStore raises
            // OptionsSaved with the same instance PluginOptionsPageView hands back as the new view,
            // so a status written here reaches the page without a reload.
            RefreshStatus(options, Probe(options));
        }

        /// <summary>
        /// Runs the local proxy check for the page, on demand and never on a timer.
        /// </summary>
        /// <remarks>
        /// Blocking is acceptable and a timer is not, which is the whole shape of this design. The
        /// probe talks only to the proxy, bounded by its own five-second timeout, and it runs at the
        /// two moments a human is looking at the page. Nothing routes on the result — see
        /// <see cref="ProxyProbe"/> — so there is no reason to keep one warm in the background, and
        /// every reason not to: a periodic check is what would give the plugin an opinion about
        /// reachability that its own routing then has to honour.
        /// </remarks>
        private ProbeResult Probe(PluginOptions options)
        {
            try
            {
                var settings = ProxySettings.FromOptions(options);

                // Task.Run keeps the blocking wait off any ambient synchronization context.
                return Task.Run(() => ProxyProbe.RunAsync(settings, CancellationToken.None))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                // Never let a diagnostic cost the user their settings - those are already written.
                if (_logger != null)
                {
                    _logger.ErrorException("Proxy Router: the proxy check failed to run.", ex);
                }

                return new ProbeResult(ProbeVerdict.Failed, LocalizedText.Of("ProbeFailed", ex.Message));
            }
        }

        /// <summary>
        /// Fills the two read-only status lines shown at the top of the settings page.
        /// </summary>
        private void RefreshStatus(PluginOptions options, ProbeResult probe)
        {
            // Patch status first: without the patch nothing else on this page has any effect, and
            // saying so plainly beats letting the user believe a green proxy means traffic is routed.
            if (HttpHandlerPatch.IsApplied && HttpHandlerPatch.DecorationFailureReason == null)
            {
                options.PatchStatus = new StatusItem
                {
                    Status = ItemStatus.Succeeded,
                    Caption = Localizer.Get("PatchCaption"),
                    StatusText = Localizer.Get("PatchActive")
                };
            }
            else if (HttpHandlerPatch.IsApplied)
            {
                // The patch applied, but at least one handler could not be given the proxy. Those
                // requests are still blocked rather than leaking, so this is not "not active" — but
                // reporting plain success would claim traffic is routed that is not.
                options.PatchStatus = new StatusItem
                {
                    Status = ItemStatus.Warning,
                    Caption = Localizer.Get("PatchCaption"),
                    StatusText = Localizer.Format(
                        "PatchDegraded", HttpHandlerPatch.DecorationFailureReason)
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

            var detail = probe.Detail == null ? string.Empty : probe.Detail.Localized();

            switch (probe.Verdict)
            {
                case ProbeVerdict.Disabled:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Unavailable,
                        Caption = Localizer.Get("DisabledCaption"),
                        StatusText = Localizer.Get("DisabledText")
                    };
                    break;

                case ProbeVerdict.Ok:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Succeeded,
                        Caption = Localizer.Get("ReachableCaption"),
                        StatusText = detail
                    };
                    break;

                case ProbeVerdict.Warning:
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Warning,
                        Caption = Localizer.Get("WarningCaption"),
                        StatusText = detail
                    };
                    break;

                default:
                    // Misconfigured and Failed both mean traffic is not going where it should. The
                    // difference is that a misconfigured address is blocked outright, while an
                    // unreachable proxy simply fails to connect; the detail says which.
                    options.ProxyStatus = new StatusItem
                    {
                        Status = ItemStatus.Failed,
                        Caption = Localizer.Get("UnreachableCaption"),
                        StatusText = detail + Localizer.Get("UnreachableSuffix")
                    };
                    break;
            }
        }
    }
}
