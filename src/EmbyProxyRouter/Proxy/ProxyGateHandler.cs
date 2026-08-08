using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Localization;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Refuses the requests that would otherwise leave without the proxy.
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
        private readonly bool _proxyAttached;

        /// <param name="throttle">
        /// Shared across every gate instance by the patch, so the collapse is per destination rather
        /// than per handler — Emby caches one handler per host, which would otherwise make the
        /// throttle a no-op for exactly the case it exists for. Null disables throttling.
        /// </param>
        /// <param name="proxyAttached">
        /// Whether <paramref name="inner"/> actually carries the proxy. Fixed for the lifetime of
        /// this gate — it is settled when the handler is built and cannot change afterwards, because
        /// SocketsHttpHandler freezes its properties. No default on purpose: getting this wrong is
        /// silent, so every caller has to state which of the two handlers it is wrapping.
        /// </param>
        public ProxyGateHandler(
            HttpMessageHandler inner, ProxyState state, ILogger logger, LogThrottle throttle,
            bool proxyAttached)
            : base(inner)
        {
            _state = state;
            _logger = logger;
            _throttle = throttle;
            _proxyAttached = proxyAttached;
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
        /// Every destination that has a proxy URI is handed to .NET, which connects to the proxy or
        /// fails trying. It never falls back to a direct connection, so nothing here has to decide
        /// whether the proxy is up, and routing stays free of any reachability state.
        ///
        /// What is left for this class are the two cases where handing the request on would send it
        /// out in the clear. Both are settled facts by the time a request arrives, not judgements —
        /// see <see cref="Refusal"/>.
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
            var decision = _state.Decide(settings, uri, out reason);

            var refusal = Refusal(decision, reason, settings);
            if (refusal == null)
            {
                return null;
            }

            // The message is built even when the log line is suppressed: it is what the caller gets
            // as the failure, and every blocked request is entitled to be told why it failed
            // regardless of how many others failed the same way.
            var target = Redact(uri);
            var message = Localizer.FormatInvariant("LogBlocked", target, refusal);

            int suppressed;
            if (ShouldLog(reason, target, out suppressed))
            {
                _logger.Error(message + SuppressedNote(suppressed));
            }

            return new HttpRequestException(message);
        }

        /// <summary>Why this request cannot be sent, in English, or null to let it through.</summary>
        /// <remarks>
        /// Two cases, and neither is a routing decision — <see cref="ProxyState.Decide"/> remains the
        /// only thing that decides where a request goes. This asks the narrower question of whether
        /// *this handler* can carry out the verdict it was given.
        ///
        ///   * <see cref="RouteDecision.Blocked"/> — the proxy is switched on and its address does
        ///     not parse, so there is no URI to route to. An IWebProxy cannot express that: null
        ///     from GetProxy means "connect directly", which is the leak this plugin exists to
        ///     prevent. This is the case the gate was originally built for.
        ///   * The verdict is <see cref="RouteDecision.ViaProxy"/> but the inner handler never
        ///     received the proxy, because configuring it threw or Emby returned a handler type
        ///     that cannot take one. .NET would then send the request straight out — the same leak,
        ///     arrived at from the other end. The proxy is attached before the first request or
        ///     never, so this is a fixed property of the handler and needs no state of its own.
        /// </remarks>
        private string Refusal(RouteDecision decision, RouteReason reason, ProxySettings settings)
        {
            if (decision == RouteDecision.Blocked)
            {
                return ProxyState.Explain(reason, settings);
            }

            if (decision == RouteDecision.ViaProxy && !_proxyAttached)
            {
                return Localizer.GetInvariant("LogReasonNotAttached");
            }

            return null;
        }

        /// <summary>
        /// Whether this event should reach the log, and how many like it were suppressed.
        /// </summary>
        /// <remarks>
        /// A misconfigured proxy blocks every request, and a library scan issues thousands. Without
        /// this the one line that mattered — the first — is buried in the rest.
        ///
        /// The reason is part of the key so that a destination refused for a new reason is a new
        /// event. That still separates the two refusals <see cref="Refusal"/> can return, because a
        /// handler carrying no proxy reports the verdict it could not carry out —
        /// <see cref="RouteReason.Proxied"/> — which reaches the log through no other path.
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
