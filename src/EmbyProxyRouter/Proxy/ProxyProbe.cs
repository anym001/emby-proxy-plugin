using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    public enum ProbeVerdict
    {
        /// <summary>The plugin is switched off.</summary>
        Disabled = 0,
        /// <summary>Enabled, but the address does not parse.</summary>
        Misconfigured = 1,
        /// <summary>The proxy answered and accepted what it was given.</summary>
        Ok = 2,
        /// <summary>The proxy could not be reached, or is not what it claims to be.</summary>
        Failed = 3,
        /// <summary>Reachable, but something about the configuration will not do what it looks like.</summary>
        Warning = 4
    }

    public sealed class ProbeResult
    {
        public ProbeResult(ProbeVerdict verdict, LocalizedText detail)
        {
            Verdict = verdict;
            Detail = detail;
        }

        public ProbeVerdict Verdict { get; private set; }

        /// <summary>
        /// Deferred rather than rendered: the settings page shows it in the dashboard language and
        /// the log writes it in English.
        /// </summary>
        public LocalizedText Detail { get; private set; }
    }

    /// <summary>
    /// Answers "is this address really a working proxy?" — on demand, and without telling anyone.
    /// </summary>
    /// <remarks>
    /// This is a diagnostic, not a routing input. Nothing in <see cref="ProxyState"/> consults it,
    /// and a failure here does not change where a single request goes. It exists so that saving the
    /// settings page can say whether the address works, instead of leaving the user to infer it from
    /// metadata that stops arriving.
    ///
    /// Deliberately talks to the proxy and to nobody else. There is no check URL, because the probe
    /// stops before the point where the proxy would open an outbound connection: for SOCKS5 after
    /// the authentication sub-negotiation, for an HTTPS proxy after the TLS handshake, for a plain
    /// HTTP proxy after the TCP connect. A periodic check against a third party would show that
    /// party the proxy's egress address on a fixed schedule, which is a strange thing for a plugin
    /// bought to control who sees outbound traffic — and it would make the verdict depend on a
    /// stranger's uptime.
    ///
    /// The honest limit of that: it cannot prove the proxy actually forwards traffic. A SOCKS5
    /// server that authenticates and then refuses every CONNECT still probes Ok. Proving otherwise
    /// requires sending something through to a destination, and no destination is free of the two
    /// problems above. Reaching the proxy and being accepted by it is what can be established
    /// locally, so that is what is claimed and no more.
    /// </remarks>
    public static class ProxyProbe
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        public static async Task<ProbeResult> RunAsync(ProxySettings settings, CancellationToken cancellationToken)
        {
            if (settings == null || !settings.Enabled)
            {
                return new ProbeResult(ProbeVerdict.Disabled, LocalizedText.Of("ProbeDisabled"));
            }

            var endpoint = settings.Endpoint;
            if (endpoint == null)
            {
                return new ProbeResult(
                    ProbeVerdict.Misconfigured,
                    settings.ConfigError ?? LocalizedText.Of("ProbeAddressInvalid"));
            }

            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                using (var tcp = new TcpClient())
                {
                    timeout.CancelAfter(Timeout);

                    try
                    {
                        await tcp.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return Failed(LocalizedText.Of(
                            "ProbeTcpTimeout", endpoint.Host, endpoint.Port, (int)Timeout.TotalSeconds));
                    }
                    catch (Exception ex)
                    {
                        return Failed(LocalizedText.Of(
                            "ProbeTcpFailed", endpoint.Host, endpoint.Port, ex.Message));
                    }

                    switch (endpoint.Scheme)
                    {
                        case ProxyScheme.Socks5:
                            return await Socks5Async(tcp, endpoint, timeout.Token).ConfigureAwait(false);

                        case ProxyScheme.Https:
                            return await TlsAsync(tcp, endpoint, settings, timeout.Token).ConfigureAwait(false);

                        default:
                            // A plain HTTP proxy answers nothing until it is given a request to
                            // forward, and forwarding is exactly what this probe declines to do.
                            // So the claim stops at the connect, and says so rather than implying
                            // more than was established.
                            return new ProbeResult(
                                ProbeVerdict.Ok,
                                LocalizedText.Of("ProbeTcpOnlyOk", endpoint.Describe()));
                    }
                }
            }
            catch (Exception ex)
            {
                return Failed(LocalizedText.Of("ProbeFailed", ex.GetBaseException().Message));
            }
        }

        /// <summary>
        /// SOCKS5 greeting and, when credentials are configured, the RFC1929 sub-negotiation.
        /// </summary>
        /// <remarks>
        /// Stops before the CONNECT request, so the proxy never opens an outbound connection and
        /// nothing beyond it is contacted.
        ///
        /// Both methods are offered, the way .NET's own client does, so that the reply says which
        /// one the proxy chose. That distinction is the point: a proxy answering "no authentication"
        /// to a configuration that carries a username produces a setup which looks authenticated and
        /// is not — the same trap that makes ProxyEndpoint move credentials out of the URI — and
        /// offering only the authenticated method would hide it behind a bare rejection.
        /// </remarks>
        private static async Task<ProbeResult> Socks5Async(
            TcpClient tcp, ProxyEndpoint endpoint, CancellationToken cancellationToken)
        {
            var stream = tcp.GetStream();
            var credential = endpoint.Credential;
            var wantAuth = credential != null && !string.IsNullOrEmpty(credential.UserName);

            var greeting = wantAuth
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(greeting, 0, greeting.Length, cancellationToken).ConfigureAwait(false);

            var reply = new byte[2];
            if (!await ReadExactAsync(stream, reply, cancellationToken).ConfigureAwait(false) ||
                reply[0] != 0x05)
            {
                return Failed(LocalizedText.Of("ProbeNotSocks5", endpoint.Host, endpoint.Port));
            }

            if (reply[1] == 0xFF)
            {
                return Failed(LocalizedText.Of("ProbeSocks5NoMethod"));
            }

            if (reply[1] == 0x00)
            {
                return wantAuth
                    ? new ProbeResult(ProbeVerdict.Warning, LocalizedText.Of("ProbeSocks5AuthIgnored"))
                    : new ProbeResult(ProbeVerdict.Ok, LocalizedText.Of("ProbeSocks5Ok", endpoint.Describe()));
            }

            if (reply[1] != 0x02 || !wantAuth)
            {
                return Failed(LocalizedText.Of("ProbeSocks5Method", reply[1]));
            }

            var user = Encoding.UTF8.GetBytes(credential.UserName);
            var password = Encoding.UTF8.GetBytes(credential.Password ?? string.Empty);
            if (user.Length > 255 || password.Length > 255)
            {
                return Failed(LocalizedText.Of("ProbeSocks5CredentialTooLong"));
            }

            var request = new byte[3 + user.Length + password.Length];
            request[0] = 0x01;
            request[1] = (byte)user.Length;
            Buffer.BlockCopy(user, 0, request, 2, user.Length);
            request[2 + user.Length] = (byte)password.Length;
            Buffer.BlockCopy(password, 0, request, 3 + user.Length, password.Length);
            await stream.WriteAsync(request, 0, request.Length, cancellationToken).ConfigureAwait(false);

            var authReply = new byte[2];
            if (!await ReadExactAsync(stream, authReply, cancellationToken).ConfigureAwait(false))
            {
                return Failed(LocalizedText.Of("ProbeSocks5Truncated"));
            }

            return authReply[1] == 0x00
                ? new ProbeResult(ProbeVerdict.Ok, LocalizedText.Of("ProbeSocks5AuthOk", endpoint.Describe()))
                : Failed(LocalizedText.Of("ProbeSocks5AuthRejected", credential.UserName));
        }

        /// <summary>
        /// For an HTTPS proxy: the TLS handshake, which is what a plain connect cannot check.
        /// </summary>
        /// <remarks>
        /// Honours "ignore certificate validation" so the probe agrees with what the real traffic
        /// will do — a probe that refused a certificate the routed requests accept would report a
        /// broken proxy that works.
        /// </remarks>
        private static async Task<ProbeResult> TlsAsync(
            TcpClient tcp, ProxyEndpoint endpoint, ProxySettings settings, CancellationToken cancellationToken)
        {
            try
            {
                using (var ssl = new SslStream(
                           tcp.GetStream(), leaveInnerStreamOpen: true,
                           userCertificateValidationCallback: (a, b, c, errors) =>
                               errors == SslPolicyErrors.None || settings.IgnoreCertificateValidation))
                {
                    await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = endpoint.Host },
                        cancellationToken).ConfigureAwait(false);

                    return new ProbeResult(
                        ProbeVerdict.Ok, LocalizedText.Of("ProbeTlsOk", endpoint.Describe()));
                }
            }
            catch (OperationCanceledException)
            {
                return Failed(LocalizedText.Of("ProbeTlsTimeout", endpoint.Host, endpoint.Port));
            }
            catch (Exception ex)
            {
                return Failed(LocalizedText.Of("ProbeTlsFailed", ex.GetBaseException().Message));
            }
        }

        /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes, or gives up.</summary>
        private static async Task<bool> ReadExactAsync(
            NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        private static ProbeResult Failed(LocalizedText detail)
        {
            return new ProbeResult(ProbeVerdict.Failed, detail);
        }
    }
}
