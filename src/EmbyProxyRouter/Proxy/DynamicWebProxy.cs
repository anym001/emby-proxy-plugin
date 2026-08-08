using System;
using System.Net;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// An <see cref="IWebProxy"/> that answers from current configuration on every call.
    /// </summary>
    /// <remarks>
    /// This indirection is what makes the plugin reconfigurable without an Emby restart, and it is
    /// forced by two facts about how Emby 4.9.5.0 and .NET behave:
    ///
    ///   * CoreHttpClientManager caches one HttpClient (and its handler) per
    ///     host+compression+userinfo+timeout key in a ConcurrentDictionary that is never evicted.
    ///   * SocketsHttpHandler freezes its properties after the first request — assigning Proxy later
    ///     throws InvalidOperationException.
    ///
    /// So a handler created once keeps whatever proxy object it was given, forever. Handing it a
    /// static WebProxy would mean every settings change needed a server restart. Handing it this
    /// object instead works because .NET calls GetProxy() per request — verified experimentally.
    /// </remarks>
    public sealed class DynamicWebProxy : IWebProxy
    {
        private readonly ProxyState _state;

        public DynamicWebProxy(ProxyState state)
        {
            _state = state;
        }

        /// <summary>
        /// Credentials for the proxy itself.
        /// </summary>
        /// <remarks>
        /// This is the only channel .NET honours for SOCKS5 authentication; userinfo inside the
        /// proxy URI is ignored outright. For HTTP(S) proxies .NET sends these reactively, after the
        /// proxy answers 407 — it does not authenticate pre-emptively.
        /// </remarks>
        public ICredentials Credentials
        {
            get
            {
                var endpoint = _state.Settings.Endpoint;
                return endpoint == null ? null : endpoint.Credential;
            }
            set
            {
                // Credentials follow the plugin configuration; ignore external assignment.
            }
        }

        public Uri GetProxy(Uri destination)
        {
            // One snapshot for both the verdict and the endpoint it refers to. Reading Settings
            // again after Decide could straddle a configuration change and pair a ViaProxy verdict
            // with an endpoint that is already null.
            var settings = _state.Settings;

            RouteReason reason;
            switch (_state.Decide(settings, destination, out reason))
            {
                case RouteDecision.ViaProxy:
                    // Decide only returns ViaProxy after establishing the endpoint is non-null.
                    return settings.Endpoint.Uri;

                default:
                    // Direct, and Blocked with it. An IWebProxy cannot express a block: null is the
                    // only other thing it can say and null means "connect directly". Blocked is also
                    // the one verdict that has no proxy URI to name even if it could — Decide reaches
                    // it precisely because the endpoint is null. So enforcement lives in
                    // ProxyGateHandler, which rejects these before the resolver is ever consulted.
                    return null;
            }
        }

        public bool IsBypassed(Uri host)
        {
            return _state.Decide(host) == RouteDecision.Direct;
        }
    }
}
