using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// The per-workspace proxy header policy (#86): the canonical HTTP hop-by-hop/forbidden header
    /// set plus the add/override/remove rule model applied to BOTH legs of the proxy fetch —
    /// request rules rewrite what the Event Server sends upstream, response rules rewrite what it
    /// returns to the plugin client. <see cref="IsForbiddenHeader"/> is the HARD guardrail: the
    /// configuration can never add, override, or remove these — the Management Client refuses such
    /// rows at save time and the runtime skips such rules (with a MIP log line) even if a stored
    /// configuration somehow carries them.
    /// </summary>
    internal static class ProxyHeaderPolicy
    {
        /// <summary>
        /// The single canonical forbidden set every proxy leg derives from (#86): the RFC 9110
        /// hop-by-hop headers plus the ones that must stay with the connection owner
        /// (Host, Content-Length, Transfer-Encoding, TE, Upgrade). The Event Server proxy STRIPS
        /// these on both legs unconditionally — no configuration can add, override or remove them —
        /// because each is either connection-scoped (Connection, Keep-Alive, TE, Trailer, Upgrade,
        /// Proxy-*) or owned by the HTTP stack itself (Content-Length, Transfer-Encoding, Host):
        /// a configured Host/Content-Length/Transfer-Encoding would desynchronize the upstream
        /// connection framing or route the request at the wrong origin. The relay additionally
        /// strips the end-to-end Connection/Keep-Alive/Upgrade pair (ProxyForwardCodec's
        /// relay/transport boundary), and the fetcher additionally sends nothing else (the
        /// HttpClientHandler owns Cookie, User-Agent, Referer, Accept-Encoding, Authorization).
        /// </summary>
        internal static readonly HashSet<string> ForbiddenHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "Proxy-Connection",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
            "Host",
            "Content-Length",
        };

        /// <summary>
        /// Whether a header name is forbidden to the header policy: the canonical set plus every
        /// <c>Proxy-*</c> pseudo-header (RFC 9110 keeps the whole prefix hop-by-hop).
        /// </summary>
        internal static bool IsForbiddenHeader(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            return ForbiddenHeaderNames.Contains(name)
                || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One configured header rule. <see cref="Remove"/> = true means "never let this header
        /// through" (removal); otherwise the rule adds or overrides the header with
        /// <see cref="Value"/> — last rule wins for one name, in configuration order.
        /// </summary>
        internal sealed class HeaderRule
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public bool Remove { get; set; }
        }

        /// <summary>
        /// Validates raw rule text into a <see cref="HeaderRule"/>: RFC 9110 field-name token
        /// grammar for the name, no CR/LF anywhere (response-header injection), and the forbidden
        /// guardrail. Returns false with the offending <paramref name="error"/> naming the header —
        /// the same message shape the Management Client shows and the unit tests pin.
        /// </summary>
        internal static bool TryBuildRule(string name, string value, bool remove, out HeaderRule rule, out string error)
        {
            rule = null;
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "header name is empty";
                return false;
            }
            name = name.Trim();
            if (!IsValidHeaderName(name))
            {
                error = $"header '{name}' is not a valid RFC 9110 field name";
                return false;
            }
            if (IsForbiddenHeader(name))
            {
                error = $"header '{name}' is reserved for the proxy connection and cannot be configured";
                return false;
            }
            if (!remove && string.IsNullOrWhiteSpace(value))
            {
                error = $"header '{name}' has no value";
                return false;
            }
            if (!remove && value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
            {
                error = $"header '{name}' value contains a line break";
                return false;
            }
            rule = new HeaderRule { Name = name, Value = remove ? null : (value ?? string.Empty), Remove = remove };
            return true;
        }

        /// <summary>
        /// RFC 9110 field-name token grammar (tchars only) — also the CR/LF injection guardrail.
        /// </summary>
        private static bool IsValidHeaderName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }
            foreach (var c in name)
            {
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') &&
                    c != '!' && c != '#' && c != '$' && c != '%' && c != '&' &&
                    c != '\'' && c != '*' && c != '+' && c != '-' && c != '.' &&
                    c != '^' && c != '_' && c != '`' && c != '|' && c != '~')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Applies configured rules to one header collection (request or response leg) in
        /// configuration order - last rule for a name wins: each rule first strips every existing
        /// entry of its name, then (unless it is a removal) appends its own single entry; rules
        /// whose name is forbidden are skipped (the store already audited them). Mutates
        /// <paramref name="headers"/> in place (one entry per configured name, the rule's own
        /// casing); the returned list is the rules actually applied (names + action) for audit
        /// lines. Removal of an absent name is a no-op and is not recorded.
        /// </summary>
        internal static List<string> ApplyRules(List<ProxyFetchHeader> headers, IList<HeaderRule> rules)
        {
            var applied = new List<string>();
            if (headers == null || rules == null)
            {
                return applied;
            }
            foreach (var rule in rules)
            {
                if (rule == null || IsForbiddenHeader(rule.Name))
                {
                    continue;
                }
                var removed = RemoveAll(headers, rule.Name);
                if (rule.Remove)
                {
                    if (removed)
                    {
                        applied.Add("-" + rule.Name);
                    }

                    continue;
                }

                headers.Add(new ProxyFetchHeader { Name = rule.Name, Value = rule.Value });
                applied.Add(rule.Name + "=" + rule.Value);
            }
            return applied;
        }

        /// <summary>
        /// Removes every entry whose name matches case-insensitively; true if anything went.
        /// </summary>
        private static bool RemoveAll(List<ProxyFetchHeader> headers, string name)
        {
            var removed = false;
            for (var i = headers.Count - 1; i >= 0; i--)
            {
                var header = headers[i];
                if (header != null && string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    headers.RemoveAt(i);
                    removed = true;
                }
            }

            return removed;
        }
    }
}
