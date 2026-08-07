using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Localization;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Enforces the fail-closed policy and makes every routing decision visible in the Emby log.
    /// </summary>
    /// <remarks>
    /// Emby's handler factory is typed to return HttpMessageHandler and CoreHttpClientManager only
    /// ever passes the result to <c>new HttpClient(handler)</c> without casting it, so wrapping the
    /// real SocketsHttpHandler in a DelegatingHandler is safe and gives a place to refuse a request
    /// outright. IWebProxy alone could not do this: it can only choose a proxy or return null, and
    /// returning null means "go direct" — a leak, not a block.
    /// </remarks>
    public sealed class ProxyGateHandler : DelegatingHandler
    {
        private readonly ProxyState _state;
        private readonly ILogger _logger;
        private readonly LogThrottle _throttle;

        /// <param name="throttle">
        /// Shared across every gate instance by the patch, so the collapse is per destination rather
        /// than per handler — Emby caches one handler per host, which would otherwise make the
        /// throttle a no-op for exactly the case it exists for. Null disables throttling.
        /// </param>
        public ProxyGateHandler(
            HttpMessageHandler inner, ProxyState state, ILogger logger, LogThrottle throttle)
            : base(inner)
        {
            _state = state;
            _logger = logger;
            _throttle = throttle;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var blocked = Gate(request);
            if (blocked != null)
            {
                return Task.FromException<HttpResponseMessage>(blocked);
            }

            return base.SendAsync(request, cancellationToken);
        }

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var blocked = Gate(request);
            if (blocked != null)
            {
                throw blocked;
            }

            return base.Send(request, cancellationToken);
        }

        /// <summary>Returns the exception to fail with, or null to let the request proceed.</summary>
        private Exception Gate(HttpRequestMessage request)
        {
            var uri = request == null ? null : request.RequestUri;
            if (uri == null)
            {
                return null;
            }

            // One snapshot for the verdict and for the follow-up question below, so the two cannot
            // straddle a configuration change and disagree about the same request.
            var settings = _state.Settings;

            RouteReason reason;
            var decision = _state.Decide(settings, uri, out reason);

            switch (decision)
            {
                case RouteDecision.Blocked:
                    // The message is built even when the log line is suppressed: it is what the
                    // caller gets as the failure, and every blocked request is entitled to be told
                    // why it failed regardless of how many others failed the same way.
                    var target = Redact(uri);
                    var message = Localizer.FormatInvariant(
                        "LogBlocked", target, ProxyState.Explain(reason, settings));

                    int blockedSuppressed;
                    if (ShouldLog(decision, reason, target, out blockedSuppressed))
                    {
                        _logger.Warn(message + SuppressedNote(blockedSuppressed));
                    }

                    return new HttpRequestException(message);

                case RouteDecision.Direct:
                    // Only worth a warning when the proxy was supposed to handle this and could not.
                    // Bypass-list hits and a disabled plugin are expected, not incidents.
                    if (settings.Enabled && reason != RouteReason.Bypassed)
                    {
                        var directTarget = Redact(uri);

                        int directSuppressed;
                        if (ShouldLog(decision, reason, directTarget, out directSuppressed))
                        {
                            _logger.Warn(Localizer.FormatInvariant(
                                             "LogFailOpen",
                                             directTarget,
                                             ProxyState.Explain(reason, settings)) +
                                         SuppressedNote(directSuppressed));
                        }
                    }

                    return null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether this event should reach the log, and how many like it were suppressed.
        /// </summary>
        /// <remarks>
        /// The key carries the reason as well as the destination, so a host whose verdict changes —
        /// unreachable to misconfigured, say — reports the change immediately instead of waiting out
        /// a window opened by the previous one.
        /// </remarks>
        private bool ShouldLog(RouteDecision decision, RouteReason reason, string target, out int suppressed)
        {
            suppressed = 0;

            if (_throttle == null)
            {
                return true;
            }

            return _throttle.ShouldLog((int)decision + "|" + (int)reason + "|" + target, out suppressed);
        }

        private string SuppressedNote(int suppressed)
        {
            if (suppressed <= 0)
            {
                return string.Empty;
            }

            return Localizer.FormatInvariant(
                "LogSuppressed", suppressed, (int)_throttle.Window.TotalSeconds);
        }

        /// <summary>
        /// Logs scheme, host and port only.
        /// </summary>
        /// <remarks>
        /// Paths and query strings of metadata lookups carry title and identifier information, and
        /// often API keys. Writing them to the Emby log would undercut the point of the plugin.
        /// </remarks>
        private static string Redact(Uri uri)
        {
            return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
        }
    }
}
