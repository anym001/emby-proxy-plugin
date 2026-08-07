using System;
using EmbyProxyRouter.Localization;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// Covers the single routing authority. Every verdict the proxy resolver and the gate act on
    /// comes from <see cref="ProxyState.Decide(ProxySettings, Uri, out RouteReason)"/>, so the
    /// matrix below is the plugin's actual behaviour, stated once.
    /// </summary>
    public class ProxyStateTests
    {
        private static readonly Uri Public = new Uri("https://api.themoviedb.org/3/movie/1");
        private static readonly Uri Lan = new Uri("http://192.168.1.50:8096/");

        /// <summary>Options are the only way in: ProxySettings has no public setters by design.</summary>
        private static ProxySettings Settings(
            bool enabled = true, string address = "socks5://proxy.example.com:1080",
            bool failOpen = false, string bypass = null)
        {
            return ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = enabled,
                ProxyAddress = address,
                AllowDirectWhenProxyUnavailable = failOpen,
                BypassList = bypass ?? string.Empty
            });
        }

        private static ProxyState StateWith(ProxySettings settings, ProxyHealth health)
        {
            var state = new ProxyState();
            state.Apply(settings);

            if (health != ProxyHealth.Unknown)
            {
                state.SetHealth(health, LocalizedText.Of("HealthTcpOnlyOk"));
            }

            return state;
        }

        // --- The decision matrix ---------------------------------------------------------------

        [Fact]
        public void ADisabledPluginSendsEverythingDirect()
        {
            var settings = Settings(enabled: false);
            var state = StateWith(settings, ProxyHealth.Unknown);

            RouteReason reason;
            Assert.Equal(RouteDecision.Direct, state.Decide(settings, Public, out reason));
            Assert.Equal(RouteReason.Disabled, reason);
        }

        [Fact]
        public void AReachableProxyTakesPublicTraffic()
        {
            var settings = Settings();
            var state = StateWith(settings, ProxyHealth.Reachable);

            RouteReason reason;
            Assert.Equal(RouteDecision.ViaProxy, state.Decide(settings, Public, out reason));
            Assert.Equal(RouteReason.ProxyReachable, reason);
        }

        /// <summary>
        /// The default, and the reason the plugin exists: an unreachable proxy blocks rather than
        /// quietly falling back to a direct connection.
        /// </summary>
        [Theory]
        [InlineData(ProxyHealth.Unreachable, RouteReason.ProxyUnreachable)]
        [InlineData(ProxyHealth.Unknown, RouteReason.ProxyNotChecked)]
        public void FailClosedBlocksWhenTheProxyIsNotConfirmedUp(ProxyHealth health, RouteReason expected)
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, health);

            RouteReason reason;
            Assert.Equal(RouteDecision.Blocked, state.Decide(settings, Public, out reason));
            Assert.Equal(expected, reason);
        }

        [Theory]
        [InlineData(ProxyHealth.Unreachable, RouteReason.ProxyUnreachable)]
        [InlineData(ProxyHealth.Unknown, RouteReason.ProxyNotChecked)]
        public void FailOpenGoesDirectWhenTheProxyIsNotConfirmedUp(ProxyHealth health, RouteReason expected)
        {
            var settings = Settings(failOpen: true);
            var state = StateWith(settings, health);

            RouteReason reason;
            Assert.Equal(RouteDecision.Direct, state.Decide(settings, Public, out reason));
            Assert.Equal(expected, reason);
        }

        /// <summary>
        /// A misconfigured proxy is not a licence to send everything in the clear.
        /// </summary>
        [Fact]
        public void AnUnparseableAddressBlocksUnderFailClosed()
        {
            var settings = Settings(address: "not a proxy at all");
            var state = StateWith(settings, ProxyHealth.Unknown);

            Assert.Null(settings.Endpoint);
            Assert.NotNull(settings.ConfigError);

            RouteReason reason;
            Assert.Equal(RouteDecision.Blocked, state.Decide(settings, Public, out reason));
            Assert.Equal(RouteReason.Misconfigured, reason);
        }

        [Fact]
        public void AnUnparseableAddressGoesDirectUnderFailOpen()
        {
            var settings = Settings(address: "not a proxy at all", failOpen: true);
            var state = StateWith(settings, ProxyHealth.Unknown);

            RouteReason reason;
            Assert.Equal(RouteDecision.Direct, state.Decide(settings, Public, out reason));
            Assert.Equal(RouteReason.Misconfigured, reason);
        }

        /// <summary>
        /// The bypass list is consulted before health, so a dead proxy never cuts the server off
        /// from its own network.
        /// </summary>
        [Fact]
        public void BypassedDestinationsGoDirectEvenWhenTheProxyIsDown()
        {
            var settings = Settings(failOpen: false);
            var state = StateWith(settings, ProxyHealth.Unreachable);

            RouteReason reason;
            Assert.Equal(RouteDecision.Direct, state.Decide(settings, Lan, out reason));
            Assert.Equal(RouteReason.Bypassed, reason);
        }

        // --- Snapshot handling -----------------------------------------------------------------

        /// <summary>
        /// A new configuration may point at a different proxy entirely, so the previous verdict
        /// cannot carry over — under fail-closed that deliberately blocks until the next check.
        /// </summary>
        [Fact]
        public void ApplyingSettingsInvalidatesThePreviousHealthVerdict()
        {
            var state = new ProxyState();
            state.Apply(Settings());
            state.SetHealth(ProxyHealth.Reachable, LocalizedText.Of("HealthTcpOnlyOk"));

            Assert.Equal(ProxyHealth.Reachable, state.Health);

            state.Apply(Settings(address: "socks5://other.example.com:1080"));

            Assert.Equal(ProxyHealth.Unknown, state.Health);
            Assert.Null(state.LastCheckUtc);
            Assert.Null(state.LastCheckDetail);
        }

        [Fact]
        public void SetHealthReportsOnlyRealChanges()
        {
            var state = new ProxyState();
            state.Apply(Settings());

            Assert.True(state.SetHealth(ProxyHealth.Reachable, LocalizedText.Of("HealthTcpOnlyOk")));
            Assert.False(state.SetHealth(ProxyHealth.Reachable, LocalizedText.Of("HealthAllHttpOk", 2, "p", 5)));
            Assert.True(state.SetHealth(ProxyHealth.Unreachable, LocalizedText.Of("HealthAddressInvalid")));
        }

        /// <summary>
        /// The verdict, its timestamp and its detail are swapped as one unit, so they can never be
        /// read half-updated.
        /// </summary>
        [Fact]
        public void TheHealthSnapshotIsPublishedAsAWhole()
        {
            var state = new ProxyState();
            state.Apply(Settings());

            var before = DateTime.UtcNow.AddSeconds(-1);
            state.SetHealth(ProxyHealth.Unreachable, LocalizedText.Of("HealthTcpFailed", "p", 1080, "refused"));

            Assert.Equal(ProxyHealth.Unreachable, state.Health);
            Assert.Contains("refused", state.LastCheckDetail.Invariant());
            Assert.True(state.LastCheckUtc.HasValue);
            Assert.True(state.LastCheckUtc.Value > before);
        }

        [Fact]
        public void ANullSettingsSnapshotFallsBackToTheCurrentOne()
        {
            var state = StateWith(Settings(), ProxyHealth.Reachable);

            RouteReason reason;
            Assert.Equal(RouteDecision.ViaProxy, state.Decide(null, Public, out reason));
        }

        [Fact]
        public void AFreshStateIsDisabledRatherThanUndefined()
        {
            var state = new ProxyState();

            Assert.False(state.Settings.Enabled);
            Assert.Equal(ProxyHealth.Unknown, state.Health);
            Assert.Equal(RouteDecision.Direct, state.Decide(Public));
        }

        // --- Explain ---------------------------------------------------------------------------

        /// <summary>
        /// Every reason has to produce real text. A missing key returns the key itself, which would
        /// otherwise reach the log as "ReasonBypassed".
        /// </summary>
        [Theory]
        [InlineData(RouteReason.Disabled)]
        [InlineData(RouteReason.Misconfigured)]
        [InlineData(RouteReason.Bypassed)]
        [InlineData(RouteReason.ProxyReachable)]
        [InlineData(RouteReason.ProxyNotChecked)]
        [InlineData(RouteReason.ProxyUnreachable)]
        public void EveryReasonExplainsItselfInWords(RouteReason reason)
        {
            var text = ProxyState.Explain(reason, Settings());

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain(reason.ToString(), text);
        }

        /// <summary>
        /// The misconfigured case quotes the parse error, which is the only actionable part of it.
        /// </summary>
        [Fact]
        public void TheMisconfiguredReasonQuotesTheParseError()
        {
            var settings = Settings(address: "not a proxy at all");
            var text = ProxyState.Explain(RouteReason.Misconfigured, settings);

            Assert.Contains(settings.ConfigError.Invariant(), text);
        }

        [Fact]
        public void ExplainToleratesAMissingSnapshot()
        {
            Assert.False(string.IsNullOrWhiteSpace(
                ProxyState.Explain(RouteReason.Misconfigured, null)));
        }
    }
}
