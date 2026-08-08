using System;
using System.Globalization;
using System.Net;
using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// A parsed, validated proxy address plus its credentials, in the exact shape .NET wants them.
    /// </summary>
    public sealed class ProxyEndpoint
    {
        /// <summary>The proxy URI handed to .NET. Never carries userinfo — see <see cref="Credential"/>.</summary>
        public Uri Uri { get; private set; }

        /// <summary>Credentials, or null. Always supplied out-of-band, never inside <see cref="Uri"/>.</summary>
        public NetworkCredential Credential { get; private set; }

        public ProxyScheme Scheme { get; private set; }

        public bool IsSocks => Scheme == ProxyScheme.Socks5;

        public string Host => Uri.Host;

        public int Port => Uri.Port;

        /// <summary>Scheme, host and port, with no comment on credentials.</summary>
        public string Authority => Uri.Scheme + "://" + Uri.Host + ":" + Uri.Port;

        /// <summary>
        /// <see cref="Authority"/> plus whether credentials are configured — never the password.
        /// </summary>
        /// <remarks>
        /// Ends up embedded in a log line, so it is deferred like everything else with two
        /// audiences: the log renders it in English, the page in the dashboard language. The probe
        /// results use <see cref="Authority"/> directly instead — they already state the outcome of
        /// authentication in the sentence itself, so repeating it here would say the same thing
        /// twice.
        /// </remarks>
        public LocalizedText Describe()
        {
            return Credential == null
                ? LocalizedText.Of("DescribeNoAuth", Authority)
                : LocalizedText.Of("DescribeAuthAs", Authority, Credential.UserName);
        }

        /// <summary>
        /// Parses the configured address into something .NET will actually honour.
        /// </summary>
        /// <remarks>
        /// Two things here are not cosmetic:
        ///
        /// 1. Credentials are stripped out of the URI and moved into a NetworkCredential. .NET
        ///    silently ignores userinfo in a SOCKS proxy URI — verified against a real SOCKS5 server,
        ///    which saw the client offer only the "no authentication" method. Leaving the password in
        ///    the URI would produce a config that looks authenticated and silently is not.
        ///
        /// 2. An explicit username field always wins over userinfo embedded in the URL, so there is
        ///    one obvious answer when both are filled in.
        ///
        /// Every rejection below quotes the offending input through <see cref="Redact"/>, never
        /// directly: these errors reach the Emby log, and the URL form carries the proxy password.
        /// </remarks>
        public static bool TryParse(
            string address,
            ProxyScheme fallbackScheme,
            string username,
            string password,
            out ProxyEndpoint endpoint,
            out LocalizedText error)
        {
            endpoint = null;
            error = null;

            if (string.IsNullOrWhiteSpace(address))
            {
                error = LocalizedText.Of("ErrNoAddress");
                return false;
            }

            address = address.Trim();

            ProxyScheme scheme;
            string host;
            int port;
            string uriUser = null;
            string uriPassword = null;

            if (address.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                Uri parsed;
                if (!Uri.TryCreate(address, UriKind.Absolute, out parsed))
                {
                    error = LocalizedText.Of("ErrInvalidUrl", Redact(address));
                    return false;
                }

                if (!TryMapScheme(parsed.Scheme, out scheme))
                {
                    error = LocalizedText.Of("ErrUnsupportedScheme", parsed.Scheme);
                    return false;
                }

                host = parsed.Host;
                port = AuthorityHasPort(address) ? parsed.Port : -1;

                if (!string.IsNullOrEmpty(parsed.UserInfo))
                {
                    var split = parsed.UserInfo.Split(new[] { ':' }, 2);
                    uriUser = Uri.UnescapeDataString(split[0]);
                    uriPassword = split.Length > 1 ? Uri.UnescapeDataString(split[1]) : string.Empty;
                }
            }
            else
            {
                scheme = fallbackScheme;

                // Userinfo needs a scheme to sit behind. Without one there is no authority to anchor
                // it to, and the port split below takes the last colon in the string — which for
                // "alice:s3cret@proxy:1080" is the right one, but for "alice:s3cret@proxy" is the
                // one inside the credentials, leaving the password quoted back as the failing port.
                // Rejecting it up front removes that path and says what to do instead.
                if (address.IndexOf('@') >= 0)
                {
                    error = LocalizedText.Of("ErrUserInfoNeedsScheme");
                    return false;
                }

                var colon = address.LastIndexOf(':');
                if (colon <= 0 || colon == address.Length - 1)
                {
                    error = LocalizedText.Of("ErrNeedPort");
                    return false;
                }

                host = address.Substring(0, colon);

                // Invariant and digits-only: the current culture must not decide what a port looks
                // like, and the default NumberStyles.Integer would accept " -1" as a port and let it
                // fall through to the "no explicit port" branch below with the wrong message.
                //
                // The offending text is described rather than quoted — the one rejection in this
                // method that does not echo its input. Redact cannot help here: it works on an
                // address, and this is the tail of one, so a value it would have masked inside a
                // full address arrives here already stripped of the context that identifies it.
                // The address is in the field in front of whoever reads this; the log does not need
                // a copy of whatever they typed after the last colon.
                var portText = address.Substring(colon + 1);
                if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port))
                {
                    error = LocalizedText.Of("ErrPortNotNumber");
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(host))
            {
                error = LocalizedText.Of("ErrNoHost");
                return false;
            }

            if (port < 0)
            {
                error = LocalizedText.Of("ErrNeedExplicitPort");
                return false;
            }

            if (port < 1 || port > 65535)
            {
                error = LocalizedText.Of("ErrPortRange", port);
                return false;
            }

            // UriBuilder validates lazily: the constructor takes any host string and the Uri getter
            // is what rejects one. A TryParse that throws instead of returning false would surface
            // as an exception out of the settings page's Validate and out of OnOptionsSaved, neither
            // of which has anywhere to put it.
            Uri uri;
            try
            {
                uri = new UriBuilder(SchemeToString(scheme), host, port).Uri;
            }
            catch (Exception)
            {
                error = LocalizedText.Of("ErrInvalidHost", Redact(host));
                return false;
            }

            // The Uri that came back has to still be the host and the port that went in. UriBuilder
            // validates a host far less than it validates a scheme: one containing a path separator
            // is taken verbatim into the authority, the getter parses the result back, and the port
            // lands in the path instead of the port field. "proxy.example.com/x:8080" parsed clean
            // and produced proxy.example.com **port 80** — a proxy on a port the user never wrote,
            // with nothing anywhere saying so, and every request then failing against a port
            // nobody chose.
            //
            // Comparing the result against the input catches that whole family at once, rather than
            // the separators someone thought to blacklist today. Ordinal-ignore-case because Uri
            // lowercases a host and the host:port form arrives exactly as it was typed.
            if (uri.Port != port || !string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                error = LocalizedText.Of("ErrInvalidHost", Redact(host));
                return false;
            }

            NetworkCredential credential = null;
            if (!string.IsNullOrWhiteSpace(username))
            {
                credential = new NetworkCredential(username, password ?? string.Empty);
            }
            else if (!string.IsNullOrEmpty(uriUser))
            {
                credential = new NetworkCredential(uriUser, uriPassword ?? string.Empty);
            }

            endpoint = new ProxyEndpoint
            {
                Uri = uri,
                Credential = credential,
                Scheme = scheme
            };
            return true;
        }

        /// <summary>
        /// The address with any embedded password masked, safe to quote in a message.
        /// </summary>
        /// <remarks>
        /// Every rejection in <see cref="TryParse"/> quotes what was entered, because an address
        /// that does not parse is much easier to fix when the message shows it. But those messages
        /// are not confined to the settings page: a parse error becomes
        /// <see cref="ProxySettings.ConfigError"/>, which <c>ProxyRuntime</c> writes to the Emby log
        /// on every save, <c>ProxyState.Explain</c> embeds in every block the gate
        /// reports, and the health checker writes on every status change. The URL form of the
        /// address carries the proxy password, and an Emby log is routinely pasted into an issue.
        ///
        /// So the password comes out before the message is built. The username stays: it is already
        /// in the log by way of <see cref="Describe"/>, and it is frequently the thing that is wrong.
        ///
        /// Textual rather than through <see cref="Uri"/> on purpose — this runs precisely on the
        /// inputs <see cref="Uri"/> refused, so there is no parsed form left to ask. The userinfo is
        /// taken as everything up to the *last* <c>@</c> of the authority, because a password may
        /// contain one itself; that is exactly the input which made this necessary.
        /// </remarks>
        private static string Redact(string address)
        {
            if (string.IsNullOrEmpty(address))
            {
                return address;
            }

            int start;
            var authority = Authority(address, out start);

            var at = authority.LastIndexOf('@');
            if (at < 0)
            {
                return address;
            }

            // No colon means a username with no password — nothing secret to hide.
            var userInfo = authority.Substring(0, at);
            var colon = userInfo.IndexOf(':');
            if (colon < 0)
            {
                return address;
            }

            return address.Substring(0, start) + userInfo.Substring(0, colon + 1) + "***" +
                   address.Substring(start + at);
        }

        /// <summary>
        /// Whether the address text itself names a port.
        /// </summary>
        /// <remarks>
        /// This cannot be asked of the parsed <see cref="Uri"/>, which normalises a default port
        /// away: <c>new Uri("https://p:443/").IsDefaultPort</c> is true and the result is
        /// indistinguishable from <c>https://p/</c>. Deriving "the user gave no port" from
        /// <c>IsDefaultPort</c> therefore rejected <c>https://host:443</c> and <c>http://host:80</c>
        /// by telling the user to supply the port they had just supplied. The raw text is the only
        /// place the distinction survives.
        /// </remarks>
        private static bool AuthorityHasPort(string address)
        {
            var authority = Authority(address, out _);

            var at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                authority = authority.Substring(at + 1);
            }

            var colon = authority.LastIndexOf(':');
            if (colon < 0 || colon == authority.Length - 1)
            {
                return false;
            }

            // An IPv6 literal is bracketed, and every colon inside those brackets belongs to the
            // address rather than introducing a port.
            var bracket = authority.LastIndexOf(']');
            return colon > bracket;
        }

        /// <summary>
        /// The authority of <paramref name="address"/>, and the offset it begins at.
        /// </summary>
        /// <remarks>
        /// Textual, because both callers run on input <see cref="Uri"/> either refused or has not
        /// been asked about yet, so there is no parsed form to interrogate. Shared between them
        /// because they need the same three answers — where the scheme ends, where the path begins,
        /// and therefore what lies in between — and two copies of that are two things to keep in
        /// step. <paramref name="start"/> is what lets <see cref="Redact"/> rebuild the address
        /// around the part it masks.
        ///
        /// An address with no <c>://</c> is all authority, which is the host:port form; one that is
        /// nothing but a scheme yields the empty string, and both callers treat that as "nothing
        /// here", which is the same answer they gave before.
        /// </remarks>
        private static string Authority(string address, out int start)
        {
            var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
            start = schemeEnd < 0 ? 0 : schemeEnd + 3;
            if (start >= address.Length)
            {
                return string.Empty;
            }

            var end = address.IndexOfAny(new[] { '/', '?', '#' }, start);
            return end < 0 ? address.Substring(start) : address.Substring(start, end - start);
        }

        private static bool TryMapScheme(string value, out ProxyScheme scheme)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "http":
                    scheme = ProxyScheme.Http;
                    return true;
                case "https":
                    scheme = ProxyScheme.Https;
                    return true;
                case "socks5":
                    scheme = ProxyScheme.Socks5;
                    return true;
                default:
                    scheme = ProxyScheme.Http;
                    return false;
            }
        }

        private static string SchemeToString(ProxyScheme scheme)
        {
            switch (scheme)
            {
                case ProxyScheme.Https:
                    return "https";
                case ProxyScheme.Socks5:
                    return "socks5";
                default:
                    return "http";
            }
        }
    }
}
