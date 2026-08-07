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

        /// <param name="bypassPrivate">
        /// Opted into explicitly rather than left at its default, because these tests are about what
        /// the resolver does with a bypassed destination and the default is not to bypass one.
        /// </param>
        private static ProxyState StateWith(
            bool enabled = true, string address = "socks5://proxy.example.com:1080",
            string username = null, string password = null, bool bypassPrivate = true)
        {
            var state = new ProxyState();
            state.Apply(ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = enabled,
                ProxyAddress = address,
                Username = username ?? string.Empty,
                Password = password ?? string.Empty,
                BypassPrivateNetworks = bypassPrivate
            }));
            return state;
        }

        /// <summary>
        /// A configured proxy is named for every destination that is not bypassed. Whether it is
        /// currently up is not consulted and must not be: .NET connects to it or fails, and it is
        /// that absence of a fallback which makes the routing safe without any reachability state.
        /// </summary>
        [Fact]
        public void AConfiguredProxyIsNamedForPublicTraffic()
        {
            var resolved = new DynamicWebProxy(StateWith()).GetProxy(Public);

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
            Assert.Null(new DynamicWebProxy(StateWith()).GetProxy(Lan));
        }

        /// <summary>
        /// And with the switch at its default, the same LAN address is routed like anything else.
        /// </summary>
        [Fact]
        public void ALanDestinationIsProxiedWhenTheSwitchIsOff()
        {
            var resolved = new DynamicWebProxy(StateWith(bypassPrivate: false)).GetProxy(Lan);

            Assert.NotNull(resolved);
            Assert.Equal("proxy.example.com", resolved.Host);
        }

        [Fact]
        public void ADisabledPluginResolvesToNoProxy()
        {
            Assert.Null(new DynamicWebProxy(StateWith(enabled: false)).GetProxy(Public));
        }

        /// <summary>
        /// A misconfigured address has no URI to name, so the resolver cannot express the verdict.
        /// </summary>
        /// <remarks>
        /// This is exactly why <see cref="ProxyGateHandler"/> exists: null here would mean "connect
        /// directly" — the leak. The test pins the limitation rather than a desirable behaviour, so
        /// that anyone tempted to delete the gate finds the reason it is there.
        /// </remarks>
        [Fact]
        public void AMisconfiguredAddressLeavesTheResolverUnableToRefuse()
        {
            var state = StateWith(address: "nonsense");

            Assert.Equal(RouteDecision.Blocked, state.Decide(Public));
            Assert.Null(new DynamicWebProxy(state).GetProxy(Public));
        }

        [Fact]
        public void CredentialsComeFromTheConfiguration()
        {
            var proxy = new DynamicWebProxy(StateWith(username: "alice", password: "s3cret"));

            var credential = proxy.Credentials.GetCredential(new Uri("socks5://proxy.example.com:1080"), "");
            Assert.Equal("alice", credential.UserName);
            Assert.Equal("s3cret", credential.Password);
        }

        /// <summary>
        /// External assignment is ignored: credentials follow the plugin configuration, and a caller
        /// overwriting them would silently unauthenticate every request.
        /// </summary>
        [Fact]
        public void AssigningCredentialsFromOutsideIsIgnored()
        {
            var proxy = new DynamicWebProxy(StateWith(username: "alice", password: "s3cret"))
            {
                Credentials = null
            };

            Assert.NotNull(proxy.Credentials);
        }

        [Fact]
        public void IsBypassedAgreesWithTheRoutingDecision()
        {
            var proxy = new DynamicWebProxy(StateWith());

            Assert.True(proxy.IsBypassed(Lan));
            Assert.False(proxy.IsBypassed(Public));
        }
    }
}
