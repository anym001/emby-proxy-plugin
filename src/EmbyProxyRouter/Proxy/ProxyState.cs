using System;
using System.Threading;
using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    public enum RouteDecision
    {
        /// <summary>Send directly, bypassing the proxy.</summary>
        Direct = 0,
        ViaProxy = 1,
        /// <summary>Fail the request rather than let it escape the proxy.</summary>
        Blocked = 2
    }

    /// <summary>
    /// Why <see cref="ProxyState.Decide(ProxySettings, Uri, out RouteReason)"/> reached its verdict.
    /// </summary>
    /// <remarks>
    /// A code rather than a message. <c>Decide</c> runs up to twice per outbound request and the
    /// caller that only wants the verdict — the proxy resolver's bypass check — would otherwise pay
    /// for a culture lookup and a dictionary read whose result it discards. Translating at the point
    /// a log line is actually written also keeps the routing core free of the localization layer,
    /// which it has no other reason to know about. Turn one of these into text with
    /// <see cref="ProxyState.Explain"/>.
    /// </remarks>
    public enum RouteReason
    {
        /// <summary>The plugin is switched off; Emby behaves as if it were not installed.</summary>
        Disabled = 0,
        /// <summary>Enabled, but the configured address does not parse.</summary>
        Misconfigured = 1,
        /// <summary>The destination is on the bypass list, compiled-in or user-supplied.</summary>
        Bypassed = 2,
        /// <summary>Everything else: it goes to the proxy.</summary>
        Proxied = 3
    }

    /// <summary>
    /// The single source of truth for routing decisions, shared by the proxy and the gate handler.
    /// </summary>
    /// <remarks>
    /// Both the <see cref="DynamicWebProxy"/> and the <see cref="ProxyGateHandler"/> must reach the
    /// same verdict for a given destination, so the verdict lives in exactly one method here rather
    /// than being reimplemented on both sides.
    ///
    /// There is deliberately no reachability state here. Whether the proxy is up is not an input to
    /// routing: a configured proxy is used, and if it cannot be reached the request fails, exactly
    /// as it does for curl, for a browser, or for any other program handed a proxy address. Making
    /// it an input is what forces a plugin to poll something in order to answer, and it is only
    /// needed to decide when to *stop* using the proxy — which this plugin never does.
    /// <see cref="ProxyProbe"/> still exists, but only to tell the settings page whether the address
    /// works; nothing here consults it.
    /// </remarks>
    public sealed class ProxyState
    {
        private ProxySettings _settings = ProxySettings.Disabled();

        public ProxySettings Settings
        {
            get { return Volatile.Read(ref _settings); }
        }

        public void Apply(ProxySettings settings)
        {
            Volatile.Write(ref _settings, settings ?? ProxySettings.Disabled());
        }

        /// <summary>
        /// Decides how a single destination should be routed.
        /// </summary>
        public RouteDecision Decide(Uri destination)
        {
            RouteReason reason;
            return Decide(Settings, destination, out reason);
        }

        /// <summary>
        /// Decides how a single destination should be routed against a caller-supplied snapshot.
        /// </summary>
        /// <remarks>
        /// A caller that needs both the verdict and the settings it was based on — the proxy
        /// resolver needs the endpoint, the gate needs the bypass list — must pass the same snapshot
        /// in rather than reading <see cref="Settings"/> a second time around the call. Reading it
        /// twice can straddle a configuration change and produce a verdict from one snapshot applied
        /// to another, which is exactly the half-applied state <see cref="ProxySettings"/> is
        /// immutable to prevent.
        ///
        /// The reason is not decoration: a blocked request is entitled to be told why it failed, and
        /// "the proxy address does not parse" is the one failure that would otherwise surface as
        /// nothing at all. It is handed back as a code and only turned into text by
        /// <see cref="Explain"/>, at the point where a line is actually written.
        /// </remarks>
        public RouteDecision Decide(ProxySettings settings, Uri destination, out RouteReason reason)
        {
            if (settings == null)
            {
                settings = Settings;
            }

            if (!settings.Enabled)
            {
                reason = RouteReason.Disabled;
                return RouteDecision.Direct;
            }

            if (settings.Endpoint == null)
            {
                // Enabled but misconfigured, and the only case in the whole plugin that has to be
                // blocked rather than routed. Every other destination has a proxy URI to hand to
                // .NET, which then either reaches it or fails; here there is no URI, and an
                // IWebProxy that returns null means "connect directly" — a leak.
                reason = RouteReason.Misconfigured;
                return RouteDecision.Blocked;
            }

            if (settings.Bypass.IsBypassed(destination))
            {
                reason = RouteReason.Bypassed;
                return RouteDecision.Direct;
            }

            reason = RouteReason.Proxied;
            return RouteDecision.ViaProxy;
        }

        /// <summary>
        /// Renders a <see cref="RouteReason"/> in English, for the log.
        /// </summary>
        /// <remarks>
        /// Lives beside <see cref="Decide"/> rather than at the call site so that the verdict and its
        /// explanation cannot drift apart — adding a reason without a message would not compile past
        /// the switch below. <paramref name="settings"/> must be the same snapshot the verdict came
        /// from, because <see cref="RouteReason.Misconfigured"/> quotes the parse error out of it.
        ///
        /// English unconditionally: the only caller is <see cref="ProxyGateHandler"/>, and everything
        /// it produces goes to the Emby log or into an exception message crossing back into Emby.
        /// Neither is read in the dashboard language, so both resolve through
        /// <see cref="Localizer.GetInvariant"/> and the keys live only in en.json.
        /// </remarks>
        public static string Explain(RouteReason reason, ProxySettings settings)
        {
            switch (reason)
            {
                case RouteReason.Disabled:
                    return Localizer.GetInvariant("LogReasonDisabled");

                case RouteReason.Bypassed:
                    return Localizer.GetInvariant("LogReasonBypassed");

                case RouteReason.Proxied:
                    return Localizer.GetInvariant("LogReasonProxied");

                default:
                    var detail = settings == null ? null : settings.ConfigError;
                    return Localizer.GetInvariant("LogReasonMisconfigured") +
                           (detail != null ? ": " + detail.Invariant() : string.Empty);
            }
        }
    }
}
