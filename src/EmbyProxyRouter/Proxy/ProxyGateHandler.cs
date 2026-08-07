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

        public ProxyGateHandler(HttpMessageHandler inner, ProxyState state, ILogger logger)
            : base(inner)
        {
            _state = state;
            _logger = logger;
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

            string reason;
            var decision = _state.Decide(settings, uri, out reason);

            switch (decision)
            {
                case RouteDecision.Blocked:
                    var message = Localizer.Format("BlockedMessage", Redact(uri), reason);
                    _logger.Warn(message);
                    return new HttpRequestException(message);

                case RouteDecision.Direct:
                    // Only worth a warning when the proxy was supposed to handle this and could not.
                    // Bypass-list hits and a disabled plugin are expected, not incidents.
                    if (settings.Enabled && !settings.Bypass.IsBypassed(uri))
                    {
                        _logger.Warn(Localizer.Format("FailOpenMessage", Redact(uri), reason));
                    }
                    return null;

                default:
                    return null;
            }
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
