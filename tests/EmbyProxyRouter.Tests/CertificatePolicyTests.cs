using System;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// Covers the "ignore certificate validation" option, which is the most dangerous switch on the
    /// settings page and therefore the one most worth pinning down.
    /// </summary>
    /// <remarks>
    /// Reached by reflection rather than by making <c>HttpHandlerPatch</c> visible to tests: the
    /// shipped assembly should not carry an InternalsVisibleTo for the convenience of this file.
    /// Only <c>Configure</c> is invoked, never <c>ApplyCore</c>, so nothing here loads Harmony or
    /// needs a running server.
    /// </remarks>
    /// <remarks>
    /// The collection exists because these tests write <c>HttpHandlerPatch</c>'s static
    /// <c>_state</c>. xUnit runs the tests inside one class sequentially but different classes in
    /// parallel, so any future class touching that field has to join this collection rather than
    /// race with this one.
    /// </remarks>
    [Collection("PatchStatics")]
    public class CertificatePolicyTests
    {
        private static readonly Type PatchType =
            typeof(EmbyProxyRouter.Plugin).Assembly.GetType("EmbyProxyRouter.Patch.HttpHandlerPatch", true);

        /// <summary>Points the patch's static state at a fresh snapshot and configures a handler.</summary>
        private static SocketsHttpHandler Configure(
            bool ignoreCertificates, bool enabled = true,
            string address = "https://proxy.example.com:8443",
            RemoteCertificateValidationCallback existing = null)
        {
            var state = new ProxyState();
            state.Apply(ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = enabled,
                ProxyAddress = address,
                IgnoreCertificateValidation = ignoreCertificates
            }));

            PatchType.GetField("_state", BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, state);

            var handler = new SocketsHttpHandler();
            if (existing != null)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = existing;
            }

            PatchType.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { handler });

            return handler;
        }

        private static bool Validate(SocketsHttpHandler handler, SslPolicyErrors errors)
        {
            return handler.SslOptions.RemoteCertificateValidationCallback(
                new object(), null, null, errors);
        }

        [Fact]
        public void TheOptionOffMeansACertificateErrorStillFailsTheHandshake()
        {
            var handler = Configure(ignoreCertificates: false);

            Assert.False(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.False(Validate(handler, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.True(Validate(handler, SslPolicyErrors.None));
        }

        [Fact]
        public void TheOptionOnAcceptsACertificateError()
        {
            var handler = Configure(ignoreCertificates: true);

            Assert.True(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.True(Validate(handler, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        /// <summary>
        /// A disabled plugin has to leave Emby's TLS behaviour exactly as it found it. The option
        /// exists for proxies presenting a self-signed certificate, so with no proxy in play there
        /// is nothing for it to excuse.
        /// </summary>
        [Fact]
        public void ADisabledPluginDoesNotRelaxAnythingEvenWithTheOptionOn()
        {
            var handler = Configure(ignoreCertificates: true, enabled: false);

            Assert.False(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
        }

        /// <summary>
        /// Same for a proxy that is switched on but whose address does not parse: there is no proxy
        /// certificate to excuse, so the relaxation must not apply.
        /// </summary>
        [Fact]
        public void AMisconfiguredProxyDoesNotRelaxAnythingEither()
        {
            var handler = Configure(ignoreCertificates: true, address: "nonsense");

            Assert.False(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
        }

        /// <summary>
        /// Emby may install a callback of its own. Replacing it outright would silently drop the
        /// server's own policy — the plugin's job is to relax validation when asked to, not to
        /// replace a decision it was not asked about.
        /// </summary>
        [Fact]
        public void AnExistingCallbackIsConsultedRatherThanDiscarded()
        {
            var consulted = false;
            RemoteCertificateValidationCallback existing = (a, b, c, d) =>
            {
                consulted = true;
                return true;
            };

            var handler = Configure(ignoreCertificates: false, existing: existing);

            Assert.True(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.True(consulted);
        }

        /// <summary>
        /// With the option on, the plugin answers first and the inner callback is not reached — that
        /// is the entire point of the option, and it is what makes its breadth worth documenting.
        /// </summary>
        [Fact]
        public void TheOptionOnShortCircuitsAnExistingCallback()
        {
            var consulted = false;
            RemoteCertificateValidationCallback existing = (a, b, c, d) =>
            {
                consulted = true;
                return false;
            };

            var handler = Configure(ignoreCertificates: true, existing: existing);

            Assert.True(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.False(consulted);
        }

        /// <summary>
        /// A clean handshake never reaches the plugin's relaxation, so an existing callback keeps
        /// its say over the success path too.
        /// </summary>
        [Fact]
        public void ACleanHandshakeStillGoesThroughTheExistingCallback()
        {
            var consulted = false;
            RemoteCertificateValidationCallback existing = (a, b, c, d) =>
            {
                consulted = true;
                return false;
            };

            var handler = Configure(ignoreCertificates: true, existing: existing);

            Assert.False(Validate(handler, SslPolicyErrors.None));
            Assert.True(consulted);
        }

        /// <summary>
        /// The setting is read per callback rather than captured, because the handler's properties
        /// freeze after its first request: a value baked in at decoration time could never be
        /// revised, and toggling the option would need a server restart.
        /// </summary>
        [Fact]
        public void TheSettingIsReadPerCallbackSoTogglingItNeedsNoRestart()
        {
            var state = new ProxyState();
            var stateField = PatchType.GetField("_state", BindingFlags.Static | BindingFlags.NonPublic);
            stateField.SetValue(null, state);

            Func<bool, ProxySettings> settings = ignore => ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "https://proxy.example.com:8443",
                IgnoreCertificateValidation = ignore
            });

            state.Apply(settings(false));

            var handler = new SocketsHttpHandler();
            PatchType.GetMethod("Configure", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { handler });

            Assert.False(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));

            // Same handler, same callback - only the configuration behind it changed.
            state.Apply(settings(true));
            Assert.True(Validate(handler, SslPolicyErrors.RemoteCertificateNameMismatch));
        }

        /// <summary>
        /// Configure also attaches the proxy and turns it on; without that the gate would block
        /// traffic that nothing was routing in the first place.
        /// </summary>
        [Fact]
        public void ConfigureAttachesTheProxyToTheHandler()
        {
            var handler = Configure(ignoreCertificates: false);
            Assert.True(handler.UseProxy);
        }
    }
}
