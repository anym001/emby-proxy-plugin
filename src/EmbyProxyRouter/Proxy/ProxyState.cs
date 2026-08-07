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
        private HealthSnapshot _snapshot = HealthSnapshot.NotChecked;

        public ProxySettings Settings
        {
            get { return Volatile.Read(ref _settings); }
        }

        public ProxyHealth Health
        {
            get { return Volatile.Read(ref _snapshot).Health; }
        }

        public DateTime? LastCheckUtc
        {
            get { return Volatile.Read(ref _snapshot).CheckedUtc; }
        }

        /// <summary>The detail text of the last check, or null when none has completed.</summary>
        public string LastCheckDetail
        {
            get { return Volatile.Read(ref _snapshot).Detail; }
        }

        public void Apply(ProxySettings settings)
        {
            Volatile.Write(ref _settings, settings ?? ProxySettings.Disabled());

            // A configuration change invalidates any previous reachability verdict: it may point at
            // a completely different proxy. Under fail-closed this deliberately blocks traffic until
            // the next check succeeds, rather than trusting a result from the old configuration.
            Volatile.Write(ref _snapshot, HealthSnapshot.NotChecked);
        }

        /// <summary>Returns true when the health value changed.</summary>
        public bool SetHealth(ProxyHealth health, string detail)
        {
            var previous = Interlocked.Exchange(
                ref _snapshot, new HealthSnapshot(health, DateTime.UtcNow, detail));
            return previous.Health != health;
        }

        /// <summary>
        /// Decides how a single destination should be routed.
        /// </summary>
        public RouteDecision Decide(Uri destination)
        {
            string reason;
            return Decide(Settings, destination, out reason);
        }

        /// <summary>
        /// Decides how a single destination should be routed, and explains why.
        /// </summary>
        public RouteDecision Decide(Uri destination, out string reason)
        {
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
        /// The reason is not decoration. A user running fail-open needs to be able to see in the log
        /// that a request went out directly because the proxy was down — the reference project's
        /// habit of falling back silently is the specific behaviour this plugin rejects.
        /// </remarks>
        public RouteDecision Decide(ProxySettings settings, Uri destination, out string reason)
        {
            if (settings == null)
            {
                settings = Settings;
            }

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

            // Read once, for the same reason the settings snapshot is passed in: a check completing
            // between the two reads would otherwise pair one verdict with the other's explanation.
            var health = Health;
            if (health == ProxyHealth.Reachable)
            {
                reason = Localizer.Get("ReasonReachable");
                return RouteDecision.ViaProxy;
            }

            reason = Localizer.Get(health == ProxyHealth.Unknown
                ? "ReasonNotChecked"
                : "ReasonUnreachable");
            return settings.FailOpen ? RouteDecision.Direct : RouteDecision.Blocked;
        }

        /// <summary>
        /// The three values a check produces, swapped as one unit.
        /// </summary>
        /// <remarks>
        /// The verdict, its timestamp and its detail text are written by the check timer and read by
        /// the dashboard thread. Held as separate fields they could be observed half-updated — a new
        /// verdict next to the previous run's explanation — and only the verdict itself would carry
        /// a memory barrier. An immutable triple published with a single interlocked write makes the
        /// three consistent by construction.
        /// </remarks>
        private sealed class HealthSnapshot
        {
            public static readonly HealthSnapshot NotChecked =
                new HealthSnapshot(ProxyHealth.Unknown, null, null);

            public HealthSnapshot(ProxyHealth health, DateTime? checkedUtc, string detail)
            {
                Health = health;
                CheckedUtc = checkedUtc;
                Detail = detail;
            }

            public ProxyHealth Health { get; private set; }

            public DateTime? CheckedUtc { get; private set; }

            public string Detail { get; private set; }
        }
    }
}
