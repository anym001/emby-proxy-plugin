using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using EmbyProxyRouter.Localization;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Destinations that must never be sent through the proxy.
    /// </summary>
    /// <remarks>
    /// Deliberately does no DNS resolution. Resolving a hostname to decide whether it is private
    /// would leak the lookup to the system resolver for every request — exactly the visibility this
    /// plugin exists to prevent — and would make routing depend on DNS timing. Rules therefore match
    /// literally: IP rules against IP literals, host rules against hostnames.
    /// </remarks>
    public sealed class BypassRules
    {
        /// <summary>
        /// Applied unconditionally, on top of whatever the user's list contains.
        /// </summary>
        /// <remarks>
        /// One group only, and it is there as a safety net rather than as policy: private, loopback
        /// and link-local ranges. Clearing the text box otherwise sent LAN traffic (other Emby
        /// servers, DLNA endpoints, a local metadata cache) out through a remote proxy, and under
        /// fail-closed cut the server off from its own network whenever that proxy was down. There
        /// is no legitimate reason to proxy 127.0.0.0/8. Compiled in because the README and the
        /// settings page promise it, and a promise left to an editable default is a suggestion.
        ///
        /// Emby's licensing and Connect hosts (mb3admin.com, connect.emby.media) were once fixed
        /// entries here and deliberately are not any more: they go through the proxy like every
        /// other destination. Bypassing them meant a server whose only route outward *is* the proxy
        /// could not reach them at all, and the privacy argument for the bypass was thin — a licence
        /// check carries the key that identifies the installation either way, so sending it through
        /// a proxy hides nothing. The cost of the change is stated plainly in README.md: under
        /// fail-closed, a dead proxy now also stops Emby Premiere from validating. Do not re-add
        /// them without reopening that trade-off.
        ///
        /// Hostnames with no dot are handled in <see cref="IsBypassed"/> instead of here, because no
        /// rule syntax in this list can express "any single label".
        /// </remarks>
        public const string Always =
            "10.0.0.0/8\n" +
            "172.16.0.0/12\n" +
            "192.168.0.0/16\n" +
            "127.0.0.0/8\n" +
            "169.254.0.0/16\n" +
            "::1\n" +
            "fc00::/7\n" +
            "fe80::/10\n" +
            "localhost\n" +
            "*.local";

        private readonly List<CidrRule> _cidrRules = new List<CidrRule>();
        private readonly HashSet<string> _exactHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _suffixHosts = new List<string>();

        public IReadOnlyList<string> Errors { get; private set; }

        /// <summary>
        /// Parses the user's list, always on top of <see cref="Always"/>.
        /// </summary>
        /// <remarks>
        /// The fixed entries are merged here rather than at the call sites so that every consumer —
        /// routing, validation, the disabled state — sees the same rule set. A second Parse overload
        /// that skipped them would be one refactor away from becoming the one that gets called.
        /// </remarks>
        public static BypassRules Parse(string text)
        {
            var rules = new BypassRules();
            var errors = new List<string>();

            Add(rules, errors, Always);

            if (string.IsNullOrWhiteSpace(text))
            {
                rules.Errors = errors;
                return rules;
            }

            Add(rules, errors, text);

            rules.Errors = errors;
            return rules;
        }

        private static void Add(BypassRules rules, List<string> errors, string text)
        {
            var entries = text.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in entries)
            {
                var entry = raw.Trim();
                if (entry.Length == 0 || entry.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.IndexOf('/') >= 0)
                {
                    CidrRule cidr;
                    string cidrError;
                    if (CidrRule.TryParse(entry, out cidr, out cidrError))
                    {
                        rules._cidrRules.Add(cidr);
                    }
                    else
                    {
                        errors.Add(cidrError);
                    }
                    continue;
                }

                IPAddress ip;
                if (IPAddress.TryParse(entry, out ip))
                {
                    rules._cidrRules.Add(CidrRule.SingleAddress(ip));
                    continue;
                }

                if (entry.StartsWith("*.", StringComparison.Ordinal))
                {
                    var suffix = entry.Substring(2);
                    if (suffix.Length == 0)
                    {
                        errors.Add(Localizer.Format("ErrInvalidWildcard", entry));
                        continue;
                    }

                    // "*.example.com" matches example.com and every subdomain of it.
                    rules._suffixHosts.Add(suffix);
                    rules._exactHosts.Add(suffix);
                    continue;
                }

                rules._exactHosts.Add(entry);
            }
        }

        /// <summary>Returns true when <paramref name="destination"/> must go out directly.</summary>
        public bool IsBypassed(Uri destination)
        {
            if (destination == null)
            {
                return false;
            }

            var host = destination.Host;
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            // Uri.Host wraps IPv6 literals in brackets; IPAddress.TryParse will not accept those.
            if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
            {
                host = host.Substring(1, host.Length - 2);
            }

            // A trailing dot only makes an FQDN explicit: "nas." is "nas" and "emby.local." is
            // "emby.local". Folding it away means a rule matches whichever spelling reached us,
            // for the same reason the IPv4-mapped form is folded below — a destination must not
            // change route because of how it happened to be written.
            if (host.Length > 1 && host[host.Length - 1] == '.')
            {
                host = host.Substring(0, host.Length - 1);
            }

            IPAddress ip;
            if (IPAddress.TryParse(host, out ip))
            {
                if (MatchesAnyCidr(ip))
                {
                    return true;
                }

                // "::ffff:10.0.0.1" is the same host as "10.0.0.1", but CidrRule compares the
                // address family first, so the compiled-in IPv4 ranges would never see it — a LAN
                // address would be sent through the proxy, or blocked under fail-closed, purely
                // because of how it was spelled. Rules written in the mapped form keep working
                // because the unmapped pass above still runs first.
                if (ip.IsIPv4MappedToIPv6 && MatchesAnyCidr(ip.MapToIPv4()))
                {
                    return true;
                }

                return false;
            }

            if (_exactHosts.Contains(host))
            {
                return true;
            }

            for (var i = 0; i < _suffixHosts.Count; i++)
            {
                var suffix = _suffixHosts[i];
                if (host.Length > suffix.Length &&
                    host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    host[host.Length - suffix.Length - 1] == '.')
                {
                    return true;
                }
            }

            // A hostname with no dot in it cannot be a public DNS name. It resolves through the
            // hosts file, the machine's own search domain, or mDNS/NetBIOS — all of which are
            // meaningless to a proxy somewhere else, which has no way to look it up. So proxying
            // "nas" or "router" cannot succeed, and under fail-closed it does active harm: the
            // server loses the local services it reaches by short name.
            //
            // This is the one case .NET's own WebProxy(bypassOnLocal: true) covers that a CIDR list
            // cannot express, which is why it lives here as code rather than as an entry in Always.
            // Dotless public TLDs have existed historically, but ICANN prohibits them for gTLDs
            // (SAC053) and nothing Emby talks to uses one.
            if (host.IndexOf('.') < 0)
            {
                return true;
            }

            return false;
        }

        private bool MatchesAnyCidr(IPAddress address)
        {
            for (var i = 0; i < _cidrRules.Count; i++)
            {
                if (_cidrRules[i].Contains(address))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class CidrRule
        {
            private byte[] _network;
            private int _prefixLength;
            private AddressFamily _family;

            public static CidrRule SingleAddress(IPAddress address)
            {
                var bytes = address.GetAddressBytes();
                return new CidrRule
                {
                    _network = bytes,
                    _prefixLength = bytes.Length * 8,
                    _family = address.AddressFamily
                };
            }

            public static bool TryParse(string entry, out CidrRule rule, out string error)
            {
                rule = null;
                error = null;

                var parts = entry.Split('/');
                if (parts.Length != 2)
                {
                    error = Localizer.Format("ErrInvalidCidr", entry);
                    return false;
                }

                IPAddress address;
                if (!IPAddress.TryParse(parts[0].Trim(), out address))
                {
                    error = Localizer.Format("ErrInvalidIp", entry);
                    return false;
                }

                int prefix;
                if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix))
                {
                    error = Localizer.Format("ErrInvalidPrefix", entry);
                    return false;
                }

                var bytes = address.GetAddressBytes();
                if (prefix < 0 || prefix > bytes.Length * 8)
                {
                    error = Localizer.Format("ErrPrefixFamily", entry);
                    return false;
                }

                rule = new CidrRule
                {
                    _network = bytes,
                    _prefixLength = prefix,
                    _family = address.AddressFamily
                };
                return true;
            }

            public bool Contains(IPAddress address)
            {
                if (address.AddressFamily != _family)
                {
                    return false;
                }

                var candidate = address.GetAddressBytes();
                if (candidate.Length != _network.Length)
                {
                    return false;
                }

                var fullBytes = _prefixLength / 8;
                for (var i = 0; i < fullBytes; i++)
                {
                    if (candidate[i] != _network[i])
                    {
                        return false;
                    }
                }

                var remainingBits = _prefixLength % 8;
                if (remainingBits == 0)
                {
                    return true;
                }

                var mask = (byte)(0xFF << (8 - remainingBits));
                return (candidate[fullBytes] & mask) == (_network[fullBytes] & mask);
            }
        }
    }
}
