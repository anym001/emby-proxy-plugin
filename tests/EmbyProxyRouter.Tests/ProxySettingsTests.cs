using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    public class ProxySettingsTests
    {
        // --- The private-networks switch ----------------------------------------------------------

        /// <summary>
        /// The switch defaults to off: everything goes through the proxy unless asked otherwise.
        /// </summary>
        /// <remarks>
        /// This is also the migration behaviour, and worth pinning for that reason. An options file
        /// written before the switch existed carries no such field, so deserialization leaves the
        /// property at its default — meaning those servers start sending LAN traffic through the
        /// proxy on upgrade. README.md says so under Upgrading; if this test is ever flipped, that
        /// note has to move with it.
        /// </remarks>
        [Fact]
        public void PrivateNetworksAreProxiedByDefault()
        {
            Assert.False(new PluginOptions().BypassPrivateNetworks);
            Assert.False(ProxySettings.Disabled().Bypass.BypassPrivateNetworks);
            Assert.False(ProxySettings.FromOptions(null).Bypass.BypassPrivateNetworks);
        }

        [Theory]
        [InlineData(true, true)]
        [InlineData(false, false)]
        public void TheSwitchReachesTheRuleSet(bool configured, bool expected)
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "http://proxy.example.com:8080",
                BypassPrivateNetworks = configured
            });

            Assert.Equal(expected, settings.Bypass.BypassPrivateNetworks);
            Assert.Equal(expected, settings.Bypass.IsBypassed(new System.Uri("http://192.168.1.10/")));

            // Loopback is not the switch's to give away.
            Assert.True(settings.Bypass.IsBypassed(new System.Uri("http://127.0.0.1/")));
        }

        // --- Endpoint and errors ----------------------------------------------------------------

        [Fact]
        public void AParseFailureIsReportedOnlyWhileTheProxyIsEnabled()
        {
            var enabled = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "nonsense"
            });

            Assert.Null(enabled.Endpoint);
            Assert.NotNull(enabled.ConfigError);

            // With the plugin switched off, an unfinished address is not an error to shout about.
            var disabled = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = false,
                ProxyAddress = "nonsense"
            });

            Assert.Null(disabled.Endpoint);
            Assert.Null(disabled.ConfigError);
        }

        [Fact]
        public void NullOptionsProduceTheDisabledSnapshot()
        {
            var settings = ProxySettings.FromOptions(null);

            Assert.False(settings.Enabled);
            Assert.Null(settings.Endpoint);
            Assert.NotNull(settings.Bypass);
        }

        /// <summary>
        /// The disabled snapshot still carries the unconditional rules, so a caller reading it
        /// before any configuration lands cannot conclude that nothing at all is bypassed.
        /// </summary>
        [Fact]
        public void TheDisabledSnapshotStillCarriesTheUnconditionalRules()
        {
            var settings = ProxySettings.Disabled();

            Assert.False(settings.Enabled);
            Assert.True(settings.Bypass.IsBypassed(new System.Uri("http://127.0.0.1/")));

            // And not the switchable ones, matching the option's default.
            Assert.False(settings.Bypass.IsBypassed(new System.Uri("http://192.168.1.1/")));
        }

        [Fact]
        public void TheCertificateFlagCarriesThrough()
        {
            var settings = ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "https://proxy.example.com:8443",
                IgnoreCertificateValidation = true
            });

            Assert.True(settings.IgnoreCertificateValidation);
        }
    }
}
