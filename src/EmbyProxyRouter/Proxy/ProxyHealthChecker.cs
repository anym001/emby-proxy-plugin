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
    /// Builds its own SocketsHttpHandler for every probe, so it never travels through the patched
    /// Emby pipeline. If it used the gated pipeline, a fail-closed block would prevent the very
    /// request that is supposed to determine whether the block should still apply.
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

        private readonly ProxyState _state;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private Timer _timer;
        private bool _disposed;

        public ProxyHealthChecker(ProxyState state, ILogger logger)
        {
            _state = state;
            _logger = logger;
        }

        public void Start()
        {
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
            // Skip rather than queue: overlapping probes would only produce duplicate log noise.
            if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return _state.Health;
            }

            try
            {
                var settings = _state.Settings;

                if (!settings.Enabled)
                {
                    Update(ProxyHealth.Unknown, Localizer.Get("HealthProxyDisabled"));
                    return ProxyHealth.Unknown;
                }

                if (settings.Endpoint == null)
                {
                    Update(ProxyHealth.Unreachable,
                        settings.ConfigError ?? Localizer.Get("HealthAddressInvalid"));
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
                    Update(ProxyHealth.Reachable, Localizer.Get("HealthTcpOnlyOk"));
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
                        Update(ProxyHealth.Unreachable, Localizer.Format("HealthUrlRequired", probe.Error));
                        return ProxyHealth.Unreachable;
                    }

                    totalMs += probe.ElapsedMs;
                }

                Update(ProxyHealth.Reachable, Localizer.Format(
                    "HealthAllHttpOk", settings.HealthCheckUrls.Count, endpoint.Describe(), totalMs));
                return ProxyHealth.Reachable;
            }
            catch (Exception ex)
            {
                Update(ProxyHealth.Unreachable, Localizer.Format("HealthCheckFailed", ex.Message));
                return ProxyHealth.Unreachable;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Returns null on success, or a description of the failure.</summary>
        private async Task<string> CheckTcpAsync(ProxyEndpoint endpoint, CancellationToken cancellationToken)
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
                return Localizer.Format(
                    "HealthTcpTimeout", endpoint.Host, endpoint.Port, TcpTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                return Localizer.Format("HealthTcpFailed", endpoint.Host, endpoint.Port, ex.Message);
            }
        }

        private struct ProbeResult
        {
            public bool Success;
            public string Error;
            public long ElapsedMs;

            public static ProbeResult Failed(string error)
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
                return ProbeResult.Failed(Localizer.Format("HealthInvalidUrl", url));
            }

            var started = DateTime.UtcNow;
            try
            {
                using (var handler = CreateProbeHandler(settings))
                using (var client = new HttpClient(handler, disposeHandler: false))
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
                            return ProbeResult.Failed(Localizer.Format(
                                "HealthHttpStatus", url, (int)response.StatusCode));
                        }

                        return new ProbeResult { Success = true, ElapsedMs = elapsed };
                    }
                }
            }
            catch (Exception ex)
            {
                return ProbeResult.Failed(Localizer.Format("HealthUrlFailed", url, ex.GetBaseException().Message));
            }
        }

        private static SocketsHttpHandler CreateProbeHandler(ProxySettings settings)
        {
            var handler = new SocketsHttpHandler
            {
                // Always proxy, ignoring the bypass list: the point is to test the proxy itself.
                Proxy = new FixedProxy(settings.Endpoint),
                UseProxy = true,
                AllowAutoRedirect = false,
                ConnectTimeout = TcpTimeout
            };

            if (settings.IgnoreCertificateValidation)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (a, b, c, d) => true;
            }

            return handler;
        }

        private void Update(ProxyHealth health, string detail)
        {
            var changed = _state.SetHealth(health, detail);
            if (!changed)
            {
                _logger.Debug("Proxy status unchanged: " + health + " - " + detail);
                return;
            }

            switch (health)
            {
                case ProxyHealth.Reachable:
                    _logger.Info("Proxy status: REACHABLE - " + detail);
                    break;
                case ProxyHealth.Unreachable:
                    _logger.Warn("Proxy status: UNREACHABLE - " + detail +
                                 (_state.Settings.FailOpen
                                     ? " | Fail-open: requests will go out directly."
                                     : " | Fail-closed: affected requests will be blocked."));
                    break;
                default:
                    _logger.Info("Proxy status: " + health + " - " + detail);
                    break;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            _gate.Dispose();
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
