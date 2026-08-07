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
        /// Two groups, compiled in for the same reason: the README and the settings page promise
        /// this behaviour, and leaving a promise to an editable default makes it a suggestion.
        ///
        /// Private, loopback and link-local ranges — clearing the text box otherwise sent LAN
        /// traffic (other Emby servers, DLNA endpoints, a local metadata cache) out through a remote
        /// proxy, and under fail-closed cut the server off from its own network whenever that proxy
        /// was down. There is no legitimate reason to proxy 127.0.0.0/8.
        ///
        /// Emby's licensing and Connect hosts — not guesswork, read out of the 4.9.5.0 assemblies:
        ///   mb3admin.com        — PluginSecurityManager: /admin/service/registration/validate,
        ///                         /admin/service/appstore/register, and the plugin catalog
        ///                         (InstallationManager: www.mb3admin.com/admin/service/package/...)
        ///   connect.emby.media  — Emby.Server.Connect: https://connect.emby.media/service/
        /// Routing licence traffic through a proxy under fail-closed risks breaking Emby Premiere
        /// activation, and obscuring licence identity is not what this plugin is for. Fixing them
        /// here means nobody breaks activation with an edit whose consequence is not obvious.
        ///
        /// The trade-off, deliberately accepted: a server whose only route outward is the proxy
        /// cannot reach these hosts at all. Lifting that would need a code change, not a setting.
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
            "*.local\n" +
            "mb3admin.com\n" +
            "*.mb3admin.com\n" +
            "connect.emby.media";

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
