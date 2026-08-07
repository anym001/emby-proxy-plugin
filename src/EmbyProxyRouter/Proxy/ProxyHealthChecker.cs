using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbyProxyRouter.Localization;
using MediaBrowser.Model.Logging;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Decides whether the proxy is actually usable, on a timer and on demand.
    /// </summary>
    /// <remarks>
    /// Builds its own SocketsHttpHandler, so probes never travel through the patched Emby pipeline.
    /// If they used the gated pipeline, a fail-closed block would prevent the very request that is
    /// supposed to determine whether the block should still apply. The handler is kept for as long
    /// as the configuration it was built from — see <see cref="GetProbeHandler"/>.
    /// </remarks>
    public sealed class ProxyHealthChecker : IDisposable
    {
        /// <summary>
        /// The two probes are split by scheme because they answer different questions: plain HTTP
        /// exercises the proxy's forwarding path, HTTPS exercises its CONNECT tunnel. A proxy can
        /// serve one and refuse the other, and <see cref="CheckNowAsync"/> requires both to succeed.
        /// </summary>
        /// <remarks>
        /// captiveportal.kuketz.de is a captive-portal endpoint run for privacy reasons: it answers
        /// 204 with no body over both HTTP and HTTPS, and its operator states that request data
        /// including the IP address is discarded rather than logged. Every check URL sees the proxy's
        /// egress address on a fixed schedule, so the default should not be a large ad-funded
        /// operator. See https://www.kuketz-blog.de/android-captive-portal-check-204-http-antwort-von-captiveportal-kuketz-de/
        /// </remarks>
        public const string DefaultHttpUrl = "http://captiveportal.kuketz.de/";

        /// <summary>The HTTPS counterpart of <see cref="DefaultHttpUrl"/>, same operator.</summary>
        public const string DefaultHttpsUrl = "https://captiveportal.kuketz.de/";

        private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Keeps a pooled connection to the proxy alive across check cycles.
        /// </summary>
        /// <remarks>
        /// Comfortably longer than any sane check interval, so consecutive cycles reuse the
        /// connection rather than reconnecting. The interval is capped at an hour, which this does
        /// not cover — a cadence that slow reconnects, which is the right trade at that point.
        /// </remarks>
        private static readonly TimeSpan ProbeIdleTimeout = TimeSpan.FromMinutes(5);

        private readonly ProxyState _state;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private Timer _timer;
        private bool _disposed;

        // The handler the probes run on, plus the settings snapshot it was built for. Both are only
        // ever touched while _gate is held, so they need no synchronisation of their own.
        private SocketsHttpHandler _probeHandler;
        private ProxySettings _probeSettings;

        public ProxyHealthChecker(ProxyState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            if (_timer == null)
            {
                _timer = new Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
            }

            Reschedule(TimeSpan.Zero);
        }

        /// <summary>Runs a check now and resumes the configured cadence afterwards.</summary>
        public void Reschedule()
        {
            Reschedule(TimeSpan.Zero);
        }

        private void Reschedule(TimeSpan due)
        {
            if (_timer == null || _disposed)
            {
                return;
            }

            try
            {
                _timer.Change(due, _state.Settings.HealthCheckInterval);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void OnTick(object ignored)
        {
            // Fire and forget: a timer callback must not block, and failures are handled inside.
            var unused = CheckNowAsync(CancellationToken.None);
        }

        public async Task<ProxyHealth> CheckNowAsync(CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return _state.Health;
            }

            // Skip rather than queue: overlapping probes would only produce duplicate log noise.
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return _state.Health;
            }

            try
            {
                var settings = _state.Settings;

                DiscardProbeHandlerUnless(settings);

                if (!settings.Enabled)
                {
                    Update(ProxyHealth.Unknown, LocalizedText.Of("HealthProxyDisabled"));
                    return ProxyHealth.Unknown;
                }

                if (settings.Endpoint == null)
                {
                    Update(ProxyHealth.Unreachable,
                        settings.ConfigError ?? LocalizedText.Of("HealthAddressInvalid"));
                    return ProxyHealth.Unreachable;
                }

                var endpoint = settings.Endpoint;

                var tcp = await CheckTcpAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (tcp != null)
                {
                    Update(ProxyHealth.Unreachable, tcp);
                    return ProxyHealth.Unreachable;
                }

                if (settings.HealthCheckUrls.Count == 0)
                {
                    // TCP reachability is all we were asked to verify.
                    Update(ProxyHealth.Reachable, LocalizedText.Of("HealthTcpOnlyOk"));
                    return ProxyHealth.Reachable;
                }

                // Every URL has to answer. A first-success-wins pass would stop at the plain-HTTP
                // entry and never exercise the CONNECT tunnel, so a proxy that forwards HTTP and
                // refuses CONNECT would report healthy while nearly all of Emby's traffic fails.
                long totalMs = 0;
                foreach (var url in settings.HealthCheckUrls)
                {
                    var probe = await CheckHttpAsync(settings, url, cancellationToken).ConfigureAwait(false);
                    if (!probe.Success)
                    {
                        // Stop at the first failure. The verdict cannot change any more, and probing
                        // on would only add one HTTP timeout per remaining URL to every failed cycle.
                        Update(ProxyHealth.Unreachable, LocalizedText.Of("HealthUrlRequired", probe.Error));
                        return ProxyHealth.Unreachable;
                    }

                    totalMs += probe.ElapsedMs;
                }

                Update(ProxyHealth.Reachable, LocalizedText.Of(
                    "HealthAllHttpOk", settings.HealthCheckUrls.Count, endpoint.Describe(), totalMs));
                return ProxyHealth.Reachable;
            }
            catch (Exception ex)
            {
                Update(ProxyHealth.Unreachable, LocalizedText.Of("HealthCheckFailed", ex.Message));
                return ProxyHealth.Unreachable;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Returns null on success, or a description of the failure.</summary>
        private async Task<LocalizedText> CheckTcpAsync(ProxyEndpoint endpoint, CancellationToken cancellationToken)
        {
            try
            {
                using (var client = new TcpClient())
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TcpTimeout);
                    await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token)
                        .ConfigureAwait(false);
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                return LocalizedText.Of(
                    "HealthTcpTimeout", endpoint.Host, endpoint.Port, TcpTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                return LocalizedText.Of("HealthTcpFailed", endpoint.Host, endpoint.Port, ex.Message);
            }
        }

        private struct ProbeResult
        {
            public bool Success;
            public LocalizedText Error;
            public long ElapsedMs;

            public static ProbeResult Failed(LocalizedText error)
            {
                return new ProbeResult { Success = false, Error = error };
            }
        }

        private async Task<ProbeResult> CheckHttpAsync(
            ProxySettings settings, string url, CancellationToken cancellationToken)
        {
            Uri target;
            if (!Uri.TryCreate(url, UriKind.Absolute, out target))
            {
                return ProbeResult.Failed(LocalizedText.Of("HealthInvalidUrl", url));
            }

            var started = DateTime.UtcNow;
            try
            {
                using (var client = new HttpClient(GetProbeHandler(settings), disposeHandler: false))
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    client.Timeout = HttpTimeout;
                    timeout.CancelAfter(HttpTimeout);

                    using (var request = new HttpRequestMessage(HttpMethod.Get, target))
                    using (var response = await client
                               .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                               .ConfigureAwait(false))
                    {
                        var elapsed = (long)(DateTime.UtcNow - started).TotalMilliseconds;
                        if (!response.IsSuccessStatusCode)
                        {
                            return ProbeResult.Failed(LocalizedText.Of(
                                "HealthHttpStatus", url, (int)response.StatusCode));
                        }

                        return new ProbeResult { Success = true, ElapsedMs = elapsed };
                    }
                }
            }
            catch (Exception ex)
            {
                return ProbeResult.Failed(LocalizedText.Of("HealthUrlFailed", url, ex.GetBaseException().Message));
            }
        }

        /// <summary>
        /// The probe handler for this settings snapshot, built once and reused.
        /// </summary>
        /// <remarks>
        /// A fresh handler per probe meant a fresh connection to the proxy for every check URL of
        /// every cycle — two full connects a minute at the default cadence, none of them reused,
        /// each one a TCP handshake (and for the HTTPS probe a TLS one) that the pool already had an
        /// answer for. Reusing one handler is also a slightly better test: it exercises the proxy
        /// the way Emby's own traffic uses it, over a pooled connection rather than a cold one.
        ///
        /// Keyed on the settings instance by reference, which is sound precisely because
        /// <see cref="ProxySettings"/> is immutable and replaced wholesale — a changed configuration
        /// is necessarily a different object, and it must not keep probing through a handler built
        /// for the old proxy or the old certificate policy. Called only under <c>_gate</c>, so the
        /// swap can never pull the handler out from under a probe in flight.
        /// </remarks>
        private SocketsHttpHandler GetProbeHandler(ProxySettings settings)
        {
            DiscardProbeHandlerUnless(settings);

            if (_probeHandler != null)
            {
                return _probeHandler;
            }

            var handler = new SocketsHttpHandler
            {
                // Always proxy, ignoring the bypass list: the point is to test the proxy itself.
                Proxy = new FixedProxy(settings.Endpoint),
                UseProxy = true,
                AllowAutoRedirect = false,
                ConnectTimeout = TcpTimeout,
                PooledConnectionIdleTimeout = ProbeIdleTimeout
            };

            if (settings.IgnoreCertificateValidation)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (a, b, c, d) => true;
            }

            _probeHandler = handler;
            _probeSettings = settings;
            return handler;
        }

        /// <summary>
        /// Drops the cached handler when it was built for a different configuration.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="CheckNowAsync"/> as well as from <see cref="GetProbeHandler"/>,
        /// because switching the proxy off returns before any probe runs and would otherwise leave
        /// the old handler — and its open connection to the old proxy — sitting there indefinitely.
        /// </remarks>
        private void DiscardProbeHandlerUnless(ProxySettings settings)
        {
            if (_probeHandler == null || ReferenceEquals(_probeSettings, settings))
            {
                return;
            }

            _probeHandler.Dispose();
            _probeHandler = null;
            _probeSettings = null;
        }

        private void Update(ProxyHealth health, LocalizedText detail)
        {
            var changed = _state.SetHealth(health, detail);
            if (!changed)
            {
                _logger.Debug("Proxy status unchanged: " + health + " - " + detail.Invariant());
                return;
            }

            switch (health)
            {
                case ProxyHealth.Reachable:
                    _logger.Info("Proxy status: REACHABLE - " + detail.Invariant());
                    break;
                case ProxyHealth.Unreachable:
                    _logger.Warn("Proxy status: UNREACHABLE - " + detail.Invariant() +
                                 (_state.Settings.FailOpen
                                     ? " | Fail-open: requests will go out directly."
                                     : " | Fail-closed: affected requests will be blocked."));
                    break;
                default:
                    _logger.Info("Proxy status: " + health + " - " + detail.Invariant());
                    break;
            }
        }

        /// <summary>
        /// Stops the cadence. A probe already in flight is left to finish.
        /// </summary>
        /// <remarks>
        /// <c>_gate</c> is deliberately not disposed. Dispose runs on the entry point's shutdown
        /// path while a probe may still be awaiting a socket, and disposing the semaphore under it
        /// would make the <c>Release</c> in <see cref="CheckNowAsync"/>'s finally block throw on a
        /// fire-and-forget task nobody observes. A SemaphoreSlim only holds a disposable resource
        /// once its AvailableWaitHandle has been used, which this one never does.
        ///
        /// The probe handler is disposed only if the gate is free, i.e. no probe is running. Taking
        /// it from a live probe would surface as an ObjectDisposedException inside
        /// <see cref="CheckNowAsync"/>, whose catch-all would dutifully log the server "unreachable"
        /// while the server is shutting down. Losing the handler to the garbage collector instead is
        /// the cheaper mistake: its connections idle out on their own.
        /// </remarks>
        public void Dispose()
        {
            _disposed = true;

            var timer = _timer;
            _timer = null;
            if (timer != null)
            {
                timer.Dispose();
            }

            if (!_gate.Wait(0))
            {
                return;
            }

            try
            {
                if (_probeHandler != null)
                {
                    _probeHandler.Dispose();
                    _probeHandler = null;
                    _probeSettings = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private sealed class FixedProxy : IWebProxy
        {
            private readonly ProxyEndpoint _endpoint;

            public FixedProxy(ProxyEndpoint endpoint)
            {
                _endpoint = endpoint;
            }

            public ICredentials Credentials
            {
                get { return _endpoint.Credential; }
                set { }
            }

            public Uri GetProxy(Uri destination)
            {
                return _endpoint.Uri;
            }

            public bool IsBypassed(Uri host)
            {
                return false;
            }
        }
    }
}
