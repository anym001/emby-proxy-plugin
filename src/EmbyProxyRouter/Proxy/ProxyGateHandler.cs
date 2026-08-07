using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Localization;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Refuses the one request an <see cref="System.Net.IWebProxy"/> cannot.
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
        /// <remarks>
        /// Exactly one verdict is blocked, and it is the only one an IWebProxy cannot express: the
        /// proxy is switched on but its address does not parse, so there is no URI to route to.
        /// Returning null from GetProxy in that state means "connect directly", which is the leak
        /// this plugin exists to prevent — hence a DelegatingHandler that can refuse outright.
        ///
        /// Every other destination is handed a proxy URI and left to .NET, which connects to the
        /// proxy or fails trying. It never falls back to a direct connection, so nothing here has to
        /// decide whether the proxy is up. That is what keeps this class small and keeps routing
        /// free of any reachability state.
        /// </remarks>
        private Exception Gate(HttpRequestMessage request)
        {
            var uri = request == null ? null : request.RequestUri;
            if (uri == null)
            {
                return null;
            }

            // One snapshot for the verdict and for the message built from it, so the two cannot
            // straddle a configuration change and disagree about the same request.
            var settings = _state.Settings;

            RouteReason reason;
            if (_state.Decide(settings, uri, out reason) != RouteDecision.Blocked)
            {
                return null;
            }

            // The message is built even when the log line is suppressed: it is what the caller gets
            // as the failure, and every blocked request is entitled to be told why it failed
            // regardless of how many others failed the same way.
            var target = Redact(uri);
            var message = Localizer.FormatInvariant(
                "LogBlocked", target, ProxyState.Explain(reason, settings));

            int suppressed;
            if (ShouldLog(reason, target, out suppressed))
            {
                _logger.Error(message + SuppressedNote(suppressed));
            }

            return new HttpRequestException(message);
        }

        /// <summary>
        /// Whether this event should reach the log, and how many like it were suppressed.
        /// </summary>
        /// <remarks>
        /// A misconfigured proxy blocks every request, and a library scan issues thousands. Without
        /// this the one line that mattered — the first — is buried in the rest.
        /// </remarks>
        private bool ShouldLog(RouteReason reason, string target, out int suppressed)
        {
            suppressed = 0;

            if (_throttle == null)
            {
                return true;
            }

            return _throttle.ShouldLog((int)reason + "|" + target, out suppressed);
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
