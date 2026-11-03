using System;
using System.Collections.Generic;

namespace WebContent
{
    /// <summary>
    /// #86: why one configured header rule is refused. <see cref="ProxyHeaderPolicy"/> is the one
    /// grammar authority; the Event Server audit lines and the Management Client's Problem column
    /// are two renderings of the same code, so a row the MC would refuse and a row the ES would
    /// skip are by construction the same rows.
    /// </summary>
    internal enum ProxyHeaderRuleError
    {
        None = 0,
        NameEmpty = 1,
        NameInvalid = 2,
        Forbidden = 3,
        ValueEmpty = 4,
        ValueLineBreak = 5,
    }

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
        /// guardrail. <see cref="ValidateRule"/> returns the machine-readable refusal code — the
        /// Event Server audit lines and the Management Client's Problem column both render it
        /// through <see cref="DescribeRuleError"/>, so save-time refusal and runtime refusal can
        /// never disagree about what is wrong with a row.
        /// </summary>
        internal static ProxyHeaderRuleError ValidateRule(string name, string value, bool remove)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return ProxyHeaderRuleError.NameEmpty;
            }

            name = name.Trim();
            if (!IsValidHeaderName(name))
            {
                return ProxyHeaderRuleError.NameInvalid;
            }

            if (IsForbiddenHeader(name))
            {
                return ProxyHeaderRuleError.Forbidden;
            }

            if (!remove && string.IsNullOrWhiteSpace(value))
            {
                return ProxyHeaderRuleError.ValueEmpty;
            }

            if (!remove && value != null && (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
            {
                return ProxyHeaderRuleError.ValueLineBreak;
            }

            return ProxyHeaderRuleError.None;
        }

        internal static bool TryBuildRule(string name, string value, bool remove, out HeaderRule rule)
        {
            rule = null;
            if (ValidateRule(name, value, remove) != ProxyHeaderRuleError.None)
            {
                return false;
            }

            rule = new HeaderRule { Name = name.Trim(), Value = remove ? null : value, Remove = remove };
            return true;
        }

        /// <summary>
        /// The human sentence for a refusal code, naming the offending header. The invariant-culture
        /// audit voice (MIP.log); the MC Problem column localizes the same codes from Strings.
        /// </summary>
        internal static string DescribeRuleError(ProxyHeaderRuleError error, string name)
        {
            switch (error)
            {
                case ProxyHeaderRuleError.NameEmpty:
                    return "header name is empty";
                case ProxyHeaderRuleError.NameInvalid:
                    return $"header '{name}' is not a valid RFC 9110 field name";
                case ProxyHeaderRuleError.Forbidden:
                    return $"header '{name}' is reserved for the proxy connection and cannot be configured";
                case ProxyHeaderRuleError.ValueEmpty:
                    return $"header '{name}' has no value";
                case ProxyHeaderRuleError.ValueLineBreak:
                    return $"header '{name}' value contains a line break";
                default:
                    return string.Empty;
            }
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
