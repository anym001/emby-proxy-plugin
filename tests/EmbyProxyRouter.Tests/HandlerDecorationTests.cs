using System;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// What the postfix hands back when it could not attach the proxy to Emby's handler.
    /// </summary>
    /// <remarks>
    /// Configuring the inner handler can fail in two ways: it throws (a SocketsHttpHandler freezes
    /// its properties after its first request, so assigning <c>Proxy</c> to one already in use is an
    /// InvalidOperationException), or the handler is of a type nothing here can attach a proxy to.
    /// Either way the request must not be sent, because the handler would send it in the clear —
    /// which is the outcome the plugin exists to prevent, and the one the dashboard's degraded
    /// status promises does not happen.
    ///
    /// Reached by reflection rather than by making <c>HttpHandlerPatch</c> visible to tests, for the
    /// same reason as <see cref="CertificatePolicyTests"/>: the shipped assembly should not carry an
    /// InternalsVisibleTo for the convenience of a test file. Only <c>Decorate</c> is invoked, never
    /// <c>ApplyCore</c>, so nothing here loads Harmony or needs a running server.
    /// </remarks>
    [Collection("PatchStatics")]
    public class HandlerDecorationTests
    {
        private static readonly Type PatchType =
            typeof(EmbyProxyRouter.Plugin).Assembly.GetType("EmbyProxyRouter.Patch.HttpHandlerPatch", true);

        private const string Destination = "https://api.themoviedb.org/3/movie/1";

        /// <summary>
        /// A handler type the patch cannot configure — neither SocketsHttpHandler nor
        /// HttpClientHandler, so there is no Proxy property to assign.
        /// </summary>
        /// <remarks>
        /// This stands in for the throwing case as well. Both arrive at the same place by design:
        /// <c>Decorate</c> records the failure and tells the gate the proxy is missing, whether
        /// <c>Configure</c> returned false or threw. Using the type that cannot be configured keeps
        /// the test deterministic and off the network — provoking the frozen-handler exception would
        /// mean actually sending a request through a real socket first.
        /// </remarks>
        private sealed class UnconfigurableHandler : HttpMessageHandler
        {
            public int Calls { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
            }
        }

        private static void SetStatic(string field, object value)
        {
            PatchType.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                .SetValue(null, value);
        }

        private static object GetStatic(string field)
        {
            return PatchType.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null);
        }

        /// <summary>Points the patch's statics at a fresh snapshot and runs Decorate.</summary>
        private static HttpMessageHandler Decorate(HttpMessageHandler inner, RecordingLogger logger)
        {
            var state = new ProxyState();
            state.Apply(ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = "socks5://proxy.example.com:1080"
            }));

            SetStatic("_state", state);
            SetStatic("_logger", logger);
            SetStatic("_decorationFailureReason", null);

            return (HttpMessageHandler)PatchType
                .GetMethod("Decorate", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { inner });
        }

        private static async Task<Exception> SendAsync(HttpMessageHandler handler, string url)
        {
            try
            {
                using (var invoker = new HttpMessageInvoker(handler))
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (await invoker.SendAsync(request, CancellationToken.None).ConfigureAwait(false))
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>
        /// The whole point: a handler the proxy could not be attached to must not carry traffic.
        /// </summary>
        [Fact]
        public async Task AHandlerThatCouldNotTakeTheProxyDoesNotCarryTraffic()
        {
            var inner = new UnconfigurableHandler();

            var error = await SendAsync(Decorate(inner, new RecordingLogger()), Destination);

            Assert.IsType<HttpRequestException>(error);
            Assert.Equal(0, inner.Calls);
        }

        /// <summary>
        /// A gate goes on regardless — returning the bare handler would be the leak itself.
        /// </summary>
        [Fact]
        public void TheHandlerIsWrappedEvenWhenItCannotBeConfigured()
        {
            var decorated = Decorate(new UnconfigurableHandler(), new RecordingLogger());

            Assert.IsType<ProxyGateHandler>(decorated);
        }

        /// <summary>
        /// And the dashboard is told, because the requests now fail and the user needs the reason.
        /// </summary>
        /// <remarks>
        /// Recorded rather than only logged: <c>DecorationFailureReason</c> is what turns the patch
        /// status from "Active" into the degraded variant. Reporting plain success here would claim
        /// traffic is routed while it is being refused.
        /// </remarks>
        [Fact]
        public void TheFailureReachesTheDashboardAndTheLog()
        {
            var logger = new RecordingLogger();

            Decorate(new UnconfigurableHandler(), logger);

            Assert.NotNull(GetStatic("_decorationFailureReason"));
            Assert.Single(logger.Warnings);
        }

        /// <summary>
        /// A handler that configures cleanly reports nothing.
        /// </summary>
        /// <remarks>
        /// The control case. Without it the three tests above would still pass if Decorate simply
        /// refused everything, which would break every install rather than fix one.
        ///
        /// Nothing is sent through it: the inner handler is a real SocketsHttpHandler and would open
        /// a socket. That the gate lets an attached handler through is covered in
        /// <see cref="ProxyGateHandlerTests"/> against a stub.
        /// </remarks>
        [Fact]
        public void AHandlerThatTakesTheProxyReportsNoFailure()
        {
            var logger = new RecordingLogger();

            using (var inner = new SocketsHttpHandler())
            {
                var decorated = Decorate(inner, logger);

                Assert.IsType<ProxyGateHandler>(decorated);
                Assert.Null(GetStatic("_decorationFailureReason"));
                Assert.Empty(logger.Warnings);
                Assert.Empty(logger.Errors);
            }
        }
    }
}
