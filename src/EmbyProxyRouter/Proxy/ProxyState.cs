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
        ProxyReachable = 3,
        /// <summary>No check has completed since the configuration was last applied.</summary>
        ProxyNotChecked = 4,
        ProxyUnreachable = 5
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

        /// <summary>The detail of the last check, or null when none has completed.</summary>
        /// <remarks>
        /// Deferred rather than rendered, because it has two audiences: the settings page shows it
        /// in the dashboard language, the log writes it in English. Keeping it unrendered also means
        /// switching the display language re-renders the detail already on the page, instead of
        /// leaving the previous language's text there until the next check overwrites it.
        /// </remarks>
        public LocalizedText LastCheckDetail
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
        public bool SetHealth(ProxyHealth health, LocalizedText detail)
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
        /// The reason is not decoration. A user running fail-open needs to be able to see in the log
        /// that a request went out directly because the proxy was down — the reference project's
        /// habit of falling back silently is the specific behaviour this plugin rejects. It is
        /// handed back as a code and only turned into text by <see cref="Explain"/>, at the point
        /// where a line is actually written.
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
                // Enabled but misconfigured. Under fail-closed this is not a reason to quietly send
                // everything in the clear — that is precisely the silent-fallback behaviour this
                // plugin is meant to avoid.
                reason = RouteReason.Misconfigured;
                return settings.FailOpen ? RouteDecision.Direct : RouteDecision.Blocked;
            }

            if (settings.Bypass.IsBypassed(destination))
            {
                reason = RouteReason.Bypassed;
                return RouteDecision.Direct;
            }

            // Read once, for the same reason the settings snapshot is passed in: a check completing
            // between the two reads would otherwise pair one verdict with the other's explanation.
            var health = Health;
            if (health == ProxyHealth.Reachable)
            {
                reason = RouteReason.ProxyReachable;
                return RouteDecision.ViaProxy;
            }

            reason = health == ProxyHealth.Unknown
                ? RouteReason.ProxyNotChecked
                : RouteReason.ProxyUnreachable;
            return settings.FailOpen ? RouteDecision.Direct : RouteDecision.Blocked;
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

                case RouteReason.Misconfigured:
                    var detail = settings == null ? null : settings.ConfigError;
                    return Localizer.GetInvariant("LogReasonMisconfigured") +
                           (detail != null ? ": " + detail.Invariant() : string.Empty);

                case RouteReason.Bypassed:
                    return Localizer.GetInvariant("LogReasonBypassed");

                case RouteReason.ProxyReachable:
                    return Localizer.GetInvariant("LogReasonReachable");

                case RouteReason.ProxyNotChecked:
                    return Localizer.GetInvariant("LogReasonNotChecked");

                default:
                    return Localizer.GetInvariant("LogReasonUnreachable");
            }
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

            public HealthSnapshot(ProxyHealth health, DateTime? checkedUtc, LocalizedText detail)
            {
                Health = health;
                CheckedUtc = checkedUtc;
                Detail = detail;
            }

            public ProxyHealth Health { get; private set; }

            public DateTime? CheckedUtc { get; private set; }

            public LocalizedText Detail { get; private set; }
        }
    }
}
