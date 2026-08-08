using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// The probe speaks a real protocol to a real socket, so it is tested against one.
    /// </summary>
    /// <remarks>
    /// A stub that returned canned verdicts would not catch the things that actually go wrong here —
    /// a byte in the wrong place, a length prefix miscounted, a read that returns short. The stub
    /// below therefore implements the server half of RFC1928/1929 rather than mocking the probe.
    ///
    /// Every case asserts that the proxy was never asked to connect anywhere: the probe's whole
    /// claim is that it establishes what it can without involving a third party, and a CONNECT
    /// request arriving at the stub would break that claim while every verdict still looked right.
    /// </remarks>
    public class ProxyProbeTests
    {
        private static ProxySettings Settings(string address, string user = null, string password = null)
        {
            return ProxySettings.FromOptions(new PluginOptions
            {
                EnableProxy = true,
                ProxyAddress = address,
                Username = user ?? string.Empty,
                Password = password ?? string.Empty
            });
        }

        private static Task<ProbeResult> Run(ProxySettings settings)
        {
            return ProxyProbe.RunAsync(settings, CancellationToken.None);
        }

        // --- Not even a socket -------------------------------------------------------------------

        [Fact]
        public async Task ADisabledPluginIsNotProbed()
        {
            var result = await Run(ProxySettings.Disabled());
            Assert.Equal(ProbeVerdict.Disabled, result.Verdict);
        }

        [Fact]
        public async Task AnUnparseableAddressReportsTheParseError()
        {
            var result = await Run(Settings("nonsense"));

            Assert.Equal(ProbeVerdict.Misconfigured, result.Verdict);
            Assert.Contains("port", result.Detail.Invariant(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ARefusedConnectionFails()
        {
            // Port 9 (discard) is reserved and nothing listens on it in the test environment.
            var result = await Run(Settings("socks5://127.0.0.1:9"));

            Assert.Equal(ProbeVerdict.Failed, result.Verdict);
            Assert.Contains("127.0.0.1", result.Detail.Invariant());
        }

        // --- SOCKS5 ------------------------------------------------------------------------------

        [Fact]
        public async Task ASocks5ProxyNeedingNoAuthIsOk()
        {
            using (var stub = new Socks5Stub(requireAuth: false, acceptCredentials: true))
            {
                var result = await Run(Settings("socks5://127.0.0.1:" + stub.Port));

                Assert.Equal(ProbeVerdict.Ok, result.Verdict);
                Assert.Equal(0, stub.ConnectRequests);
            }
        }

        [Fact]
        public async Task ASocks5ProxyAcceptingTheCredentialsIsOk()
        {
            using (var stub = new Socks5Stub(requireAuth: true, acceptCredentials: true))
            {
                var result = await Run(Settings("socks5://127.0.0.1:" + stub.Port, "alice", "s3cret"));

                Assert.Equal(ProbeVerdict.Ok, result.Verdict);
                Assert.Equal("alice", stub.SeenUser);
                Assert.Equal("s3cret", stub.SeenPassword);
                Assert.Equal(0, stub.ConnectRequests);
            }
        }

        /// <summary>
        /// Wrong credentials are the failure a bare TCP connect cannot see at all.
        /// </summary>
        [Fact]
        public async Task ASocks5ProxyRejectingTheCredentialsFails()
        {
            using (var stub = new Socks5Stub(requireAuth: true, acceptCredentials: false))
            {
                var result = await Run(Settings("socks5://127.0.0.1:" + stub.Port, "alice", "wrong"));

                Assert.Equal(ProbeVerdict.Failed, result.Verdict);
                Assert.Contains("alice", result.Detail.Invariant());
                Assert.DoesNotContain("wrong", result.Detail.Invariant());
                Assert.Equal(0, stub.ConnectRequests);
            }
        }

        /// <summary>
        /// Credentials configured against a proxy that wants none: it works, and they do nothing.
        /// </summary>
        /// <remarks>
        /// The same trap that makes ProxyEndpoint move credentials out of the URI. Reporting this as
        /// plain success would leave a setup that looks authenticated and is not; reporting it as a
        /// failure would be wrong, because traffic does flow. Hence a third verdict.
        /// </remarks>
        [Fact]
        public async Task CredentialsAProxyDoesNotWantAreReportedAsAWarning()
        {
            using (var stub = new Socks5Stub(requireAuth: false, acceptCredentials: true))
            {
                var result = await Run(Settings("socks5://127.0.0.1:" + stub.Port, "alice", "s3cret"));

                Assert.Equal(ProbeVerdict.Warning, result.Verdict);
                Assert.Equal(0, stub.ConnectRequests);
            }
        }

        [Fact]
        public async Task SomethingThatIsNotSocks5Fails()
        {
            using (var stub = new Socks5Stub(requireAuth: false, acceptCredentials: true, speakSocks: false))
            {
                var result = await Run(Settings("socks5://127.0.0.1:" + stub.Port));

                Assert.Equal(ProbeVerdict.Failed, result.Verdict);

                // The exact message matters: "does not answer as SOCKS5" and "selected a method I
                // cannot perform" are different diagnoses, and asserting on shared wording let a
                // version check be deleted without this test noticing.
                Assert.Equal("ProbeNotSocks5", result.Detail.Key);
            }
        }

        /// <summary>
        /// A port that accepts and then says nothing — which is what an HTTP proxy does.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="SomethingThatIsNotSocks5Fails"/>, and the distinction is the
        /// whole point: that stub answers with something the probe can look at and reject, while a
        /// real HTTP proxy answers with nothing at all. It is waiting for a request line, the SOCKS5
        /// greeting is not one, and neither side speaks next. Found against tinyproxy, addressed as
        /// <c>socks5://</c> — which is precisely the confusion the probe exists to resolve, and
        /// precisely where it used to say "the check could not be run".
        ///
        /// The wait is ended through the caller's token rather than by sitting out the probe's own
        /// five-second timeout. Both arrive at the same catch — what is under test is the diagnosis
        /// that catch produces, not the duration in front of it, and five seconds is a poor price
        /// for a suite that otherwise finishes in a fraction of one.
        /// </remarks>
        [Fact]
        public async Task APortThatAcceptsAndSaysNothingIsNamedAsSuch()
        {
            using (var stub = new Socks5Stub(requireAuth: false, acceptCredentials: true, silent: true))
            using (var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                var result = await ProxyProbe.RunAsync(
                    Settings("socks5://127.0.0.1:" + stub.Port), caller.Token);

                Assert.Equal(ProbeVerdict.Failed, result.Verdict);

                // The key, not the wording: the failure this replaces was a correct verdict
                // carrying a message that named nothing, so the message is the fix.
                Assert.Equal("ProbeSocks5Silent", result.Detail.Key);
                Assert.Contains("SOCKS5", result.Detail.Invariant());
                Assert.Equal(0, stub.ConnectRequests);
            }
        }

        // --- HTTP --------------------------------------------------------------------------------

        /// <summary>
        /// A plain HTTP proxy is claimed reachable and no more, because no more was established.
        /// </summary>
        [Fact]
        public async Task AnHttpProxyIsCheckedOnlyAsFarAsTheConnect()
        {
            using (var stub = new Socks5Stub(requireAuth: false, acceptCredentials: true, speakSocks: false))
            {
                var result = await Run(Settings("http://127.0.0.1:" + stub.Port));

                Assert.Equal(ProbeVerdict.Ok, result.Verdict);
                Assert.Equal(0, stub.ConnectRequests);
            }
        }

        /// <summary>
        /// The server half of RFC1928/1929, enough to answer a handshake and to notice a CONNECT.
        /// </summary>
        private sealed class Socks5Stub : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly bool _requireAuth;
            private readonly bool _accept;
            private readonly bool _speakSocks;
            private readonly bool _silent;
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private int _connectRequests;

            /// <param name="silent">
            /// Accept the connection and then say nothing at all — which is not the same as
            /// <paramref name="speakSocks"/> being false. That one answers with something that is
            /// not SOCKS5, and the probe can name it. This one answers with nothing, which is what a
            /// real HTTP proxy does when handed a greeting: it is waiting for a request line, and
            /// neither side speaks next.
            /// </param>
            public Socks5Stub(
                bool requireAuth, bool acceptCredentials, bool speakSocks = true, bool silent = false)
            {
                _requireAuth = requireAuth;
                _accept = acceptCredentials;
                _speakSocks = speakSocks;
                _silent = silent;

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Task.Run(AcceptAsync);
            }

            public int Port { get; private set; }

            public string SeenUser { get; private set; }

            public string SeenPassword { get; private set; }

            /// <summary>Must stay zero: the probe may never make the proxy dial out.</summary>
            public int ConnectRequests
            {
                get { return Volatile.Read(ref _connectRequests); }
            }

            private async Task AcceptAsync()
            {
                try
                {
                    while (true)
                    {
                        var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        var unused = Task.Run(() => HandleAsync(client));
                    }
                }
                catch (Exception)
                {
                    // The listener was disposed; that is how this loop ends.
                }
            }

            private async Task HandleAsync(TcpClient client)
            {
                try
                {
                    using (client)
                    {
                        var stream = client.GetStream();

                        if (_silent)
                        {
                            // Hold the connection open and write nothing, so the probe waits on a
                            // read that never completes. Disposing the stub closes it.
                            await Task.Delay(System.Threading.Timeout.Infinite, _stop.Token)
                                .ConfigureAwait(false);
                            return;
                        }

                        if (!_speakSocks)
                        {
                            var http = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n");
                            await stream.WriteAsync(http, 0, http.Length).ConfigureAwait(false);
                            return;
                        }

                        var head = new byte[2];
                        if (!await ReadExact(stream, head).ConfigureAwait(false))
                        {
                            return;
                        }

                        var methods = new byte[head[1]];
                        if (!await ReadExact(stream, methods).ConfigureAwait(false))
                        {
                            return;
                        }

                        if (!_requireAuth)
                        {
                            await stream.WriteAsync(new byte[] { 5, 0 }, 0, 2).ConfigureAwait(false);
                        }
                        else
                        {
                            await stream.WriteAsync(new byte[] { 5, 2 }, 0, 2).ConfigureAwait(false);

                            var version = new byte[2];
                            if (!await ReadExact(stream, version).ConfigureAwait(false))
                            {
                                return;
                            }

                            var user = new byte[version[1]];
                            if (!await ReadExact(stream, user).ConfigureAwait(false))
                            {
                                return;
                            }

                            var passwordLength = new byte[1];
                            if (!await ReadExact(stream, passwordLength).ConfigureAwait(false))
                            {
                                return;
                            }

                            var password = new byte[passwordLength[0]];
                            if (!await ReadExact(stream, password).ConfigureAwait(false))
                            {
                                return;
                            }

                            SeenUser = System.Text.Encoding.UTF8.GetString(user);
                            SeenPassword = System.Text.Encoding.UTF8.GetString(password);

                            await stream.WriteAsync(new byte[] { 1, (byte)(_accept ? 0 : 1) }, 0, 2)
                                .ConfigureAwait(false);
                        }

                        // Anything further would be a CONNECT, which the probe must never send.
                        var trailing = new byte[4];
                        if (await ReadExact(stream, trailing).ConfigureAwait(false))
                        {
                            Interlocked.Increment(ref _connectRequests);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            private static async Task<bool> ReadExact(NetworkStream stream, byte[] buffer)
            {
                var offset = 0;
                while (offset < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset)
                        .ConfigureAwait(false);
                    if (read <= 0)
                    {
                        return false;
                    }

                    offset += read;
                }

                return true;
            }

            public void Dispose()
            {
                // Releases the silent handler's endless wait as well as the accept loop.
                _stop.Cancel();
                _listener.Stop();
                _stop.Dispose();
            }
        }
    }
}
