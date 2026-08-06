using System;
using System.Threading;
using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    public enum ProxyHealth
    {
        /// <summary>No check has completed yet.</summary>
        Unknown = 0,
        Reachable = 1,
        Unreachable = 2
    }

    public enum RouteDecision
    {
        /// <summary>Send directly, bypassing the proxy.</summary>
        Direct = 0,
        ViaProxy = 1,
        /// <summary>Fail the request rather than let it escape the proxy.</summary>
        Blocked = 2
    }

    /// <summary>
    /// The single source of truth for routing decisions, shared by the proxy and the gate handler.
    /// </summary>
    /// <remarks>
    /// Both the <see cref="DynamicWebProxy"/> and the <see cref="ProxyGateHandler"/> must reach the
    /// same verdict for a given destination, so the verdict lives in exactly one method here rather
    /// than being reimplemented on both sides.
    /// </remarks>
    public sealed class ProxyState
    {
        private ProxySettings _settings = ProxySettings.Disabled();
        private int _health = (int)ProxyHealth.Unknown;

        public ProxySettings Settings
        {
            get { return Volatile.Read(ref _settings); }
        }

        public ProxyHealth Health
        {
            get { return (ProxyHealth)Volatile.Read(ref _health); }
        }

        public DateTime? LastCheckUtc { get; private set; }

        public string LastCheckDetail { get; private set; }

        public void Apply(ProxySettings settings)
        {
            Volatile.Write(ref _settings, settings ?? ProxySettings.Disabled());

            // A configuration change invalidates any previous reachability verdict: it may point at
            // a completely different proxy. Under fail-closed this deliberately blocks traffic until
            // the next check succeeds, rather than trusting a result from the old configuration.
            Volatile.Write(ref _health, (int)ProxyHealth.Unknown);
            LastCheckUtc = null;
            LastCheckDetail = Localizer.Get("NotCheckedYet");
        }

        /// <summary>Returns true when the health value changed.</summary>
        public bool SetHealth(ProxyHealth health, string detail)
        {
            var previous = (ProxyHealth)Interlocked.Exchange(ref _health, (int)health);
            LastCheckUtc = DateTime.UtcNow;
            LastCheckDetail = detail;
            return previous != health;
        }

        /// <summary>
        /// Decides how a single destination should be routed.
        /// </summary>
        public RouteDecision Decide(Uri destination)
        {
            string reason;
            return Decide(destination, out reason);
        }

        /// <summary>
        /// Decides how a single destination should be routed, and explains why.
        /// </summary>
        /// <remarks>
        /// The reason is not decoration. A user running fail-open needs to be able to see in the log
        /// that a request went out directly because the proxy was down — the reference project's
        /// habit of falling back silently is the specific behaviour this plugin rejects.
        /// </remarks>
        public RouteDecision Decide(Uri destination, out string reason)
        {
            var settings = Settings;

            if (!settings.Enabled)
            {
                reason = Localizer.Get("ReasonDisabled");
                return RouteDecision.Direct;
            }

            if (settings.Endpoint == null)
            {
                // Enabled but misconfigured. Under fail-closed this is not a reason to quietly send
                // everything in the clear — that is precisely the silent-fallback behaviour this
                // plugin is meant to avoid.
                reason = Localizer.Get("ReasonMisconfigured") +
                         (settings.ConfigError != null ? ": " + settings.ConfigError : string.Empty);
                return settings.FailOpen ? RouteDecision.Direct : RouteDecision.Blocked;
            }

            if (settings.Bypass.IsBypassed(destination))
            {
                reason = Localizer.Get("ReasonBypassed");
                return RouteDecision.Direct;
            }

            if (Health == ProxyHealth.Reachable)
            {
                reason = Localizer.Get("ReasonReachable");
                return RouteDecision.ViaProxy;
            }

            reason = Localizer.Get(Health == ProxyHealth.Unknown
                ? "ReasonNotChecked"
                : "ReasonUnreachable");
            return settings.FailOpen ? RouteDecision.Direct : RouteDecision.Blocked;
        }
    }
}
