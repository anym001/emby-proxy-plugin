using System;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// The proxy resolver and the gate must never reach different verdicts for the same
    /// destination, which is why both go through <see cref="ProxyState.Decide"/>. These tests pin
    /// the resolver's half of that.
    /// </summary>
    public class DynamicWebProxyTests
    {
        private static readonly Uri Public = new Uri("https://api.themoviedb.org/3/movie/1");
        private static readonly Uri Lan = new Uri("http://192.168.1.50:8096/");

        private static ProxySettings Settings(
            bool enabled = true, string address = "socks5://proxy.example.com:1080",
            bool failOpen = false, string username = null, string password = null)
        {
            return ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = enabled,
                ProxyAddress = address,
                AllowDirectWhenProxyUnavailable = failOpen,
                Username = username ?? string.Empty,
                Password = password ?? string.Empty
            });
        }

        private static ProxyState StateWith(ProxySettings settings, ProxyHealth health)
        {
            var state = new ProxyState();
            state.Apply(settings);

            if (health != ProxyHealth.Unknown)
            {
                state.SetHealth(health, "test");
            }

            return state;
        }

        [Fact]
        public void AReachableProxyIsNamedForPublicTraffic()
        {
            var state = StateWith(Settings(), ProxyHealth.Reachable);
            var proxy = new DynamicWebProxy(state);

            var resolved = proxy.GetProxy(Public);

            Assert.NotNull(resolved);
            Assert.Equal("socks5", resolved.Scheme);
            Assert.Equal("proxy.example.com", resolved.Host);
            Assert.Equal(1080, resolved.Port);
        }

        /// <summary>
        /// Null means "connect directly", so it is the correct answer here and only here.
        /// </summary>
        [Fact]
        public void ABypassedDestinationResolvesToNoProxy()
        {
            var state = StateWith(Settings(), ProxyHealth.Reachable);
            Assert.Null(new DynamicWebProxy(state).GetProxy(Lan));
        }

        [Fact]
        public void ADisabledPluginResolvesToNoProxy()
        {
            var state = StateWith(Settings(enabled: false), ProxyHealth.Unknown);
            Assert.Null(new DynamicWebProxy(state).GetProxy(Public));
        }

        /// <summary>
        /// A blocked verdict still names the proxy. The gate is what refuses the request; naming an
        /// unreachable proxy here means a hypothetical caller that skipped the gate fails against it
        /// rather than going out in the clear.
        /// </summary>
        [Fact]
        public void ABlockedDestinationStillNamesTheProxyRatherThanGoingDirect()
        {
            var state = StateWith(Settings(failOpen: false), ProxyHealth.Unreachable);
            Assert.NotNull(new DynamicWebProxy(state).GetProxy(Public));
        }

        /// <summary>
        /// The documented limit of the above: with no endpoint there is nothing to name.
        /// </summary>
        [Fact]
        public void AMisconfiguredProxyHasNothingToNameAndSaysSo()
        {
            var state = StateWith(Settings(address: "nonsense"), ProxyHealth.Unknown);
            Assert.Null(new DynamicWebProxy(state).GetProxy(Public));
        }

        [Fact]
        public void IsBypassedAgreesWithTheRoutingVerdict()
        {
            var state = StateWith(Settings(), ProxyHealth.Reachable);
            var proxy = new DynamicWebProxy(state);

            Assert.True(proxy.IsBypassed(Lan));
            Assert.False(proxy.IsBypassed(Public));
        }

        /// <summary>
        /// Credentials must come from here, not from the URI: .NET ignores userinfo in a SOCKS
        /// proxy URI outright and negotiates "no authentication" instead.
        /// </summary>
        [Fact]
        public void CredentialsAreExposedOutOfBandAndNeverInTheUri()
        {
            var settings = Settings(username: "alice", password: "s3cret");
            var proxy = new DynamicWebProxy(StateWith(settings, ProxyHealth.Reachable));

            var credential = proxy.Credentials.GetCredential(settings.Endpoint.Uri, "socks5");
            Assert.Equal("alice", credential.UserName);
            Assert.Equal("s3cret", credential.Password);

            Assert.Equal(string.Empty, proxy.GetProxy(Public).UserInfo);
        }

        [Fact]
        public void CredentialsAreNullWhenNoneAreConfigured()
        {
            var proxy = new DynamicWebProxy(StateWith(Settings(), ProxyHealth.Reachable));
            Assert.Null(proxy.Credentials);
        }

        /// <summary>
        /// The resolver answers from current configuration on every call — that is what makes a
        /// settings change take effect without restarting Emby, since SocketsHttpHandler freezes
        /// its properties after the first request and Emby caches handlers forever.
        /// </summary>
        [Fact]
        public void TheResolverFollowsAConfigurationChangeWithoutBeingRebuilt()
        {
            var state = new ProxyState();
            var proxy = new DynamicWebProxy(state);

            state.Apply(Settings(address: "socks5://first.example.com:1080"));
            state.SetHealth(ProxyHealth.Reachable, "up");
            Assert.Equal("first.example.com", proxy.GetProxy(Public).Host);

            state.Apply(Settings(address: "http://second.example.com:8080"));
            state.SetHealth(ProxyHealth.Reachable, "up");

            var resolved = proxy.GetProxy(Public);
            Assert.Equal("second.example.com", resolved.Host);
            Assert.Equal("http", resolved.Scheme);
        }
    }
}
