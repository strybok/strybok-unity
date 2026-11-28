using System;
using System.Collections.Generic;
using System.Text;

namespace Strybok
{
    /// <summary>
    /// "Links without an API call" of the SDK contract: which URLs are ours with the
    /// <see cref="StrybokOptions.ParameterLinks"/> option, and the lookup sent to resolve for them.
    ///
    /// Works on the URL exactly as received. A path is never decoded or normalised
    /// (<c>System.Uri</c> would collapse dot segments and re-encode escapes), and of the query only
    /// the configured payload keys, <c>link</c>, <c>apn</c>, <c>ibi</c> and the campaign values are
    /// read. Never throws.
    /// </summary>
    internal static class ParameterLinkRules
    {
        private const int MaxPayloadKeys = 20;
        private const int MaxPayloadKeyLength = 40;
        private const int MaxPayloadValueLength = 256;
        private const int MaxRoutePathLength = 512;
        private const int MaxSlugLength = 64;
        private const string Hex = "0123456789ABCDEF";

        private static readonly string[] ReservedKeys = { "st", "sd", "si", "strybok_click" };

        /// <summary>Never a link, and neither is anything under them.</summary>
        private static readonly string[] ReservedPrefixes = { "/.well-known", "/_strybok" };

        /// <summary>Paths browsers and crawlers ask for on their own; reserved exactly.</summary>
        private static readonly string[] ReservedFiles =
        {
            "/favicon.ico", "/robots.txt", "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png",
            "/sitemap.xml", "/ads.txt", "/app-ads.txt", "/security.txt", "/humans.txt", "/manifest.json",
            "/site.webmanifest", "/browserconfig.xml",
        };

        private static readonly char[] QueryOrFragment = { '?', '#' };

        /// <summary>
        /// Null when <paramref name="payloadKeys"/> is a valid option; otherwise what is wrong with
        /// it: more than 20 keys, a key twice, or a key that is not 1–40 characters of
        /// <c>A–Z a–z 0–9 _ . -</c>, starts with <c>utm_</c> in any case, or is <c>st</c>,
        /// <c>sd</c>, <c>si</c> or <c>strybok_click</c> (compared exactly).
        /// </summary>
        public static string Validate(IList<string> payloadKeys)
        {
            if (payloadKeys == null) return "PayloadKeys must be a list (it may be empty)";
            if (payloadKeys.Count > MaxPayloadKeys) return "at most " + MaxPayloadKeys + " payload keys are allowed";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in payloadKeys)
            {
                if (!IsKeyShape(key))
                {
                    return "a payload key must be 1-" + MaxPayloadKeyLength + " characters of A-Z a-z 0-9 _ . -";
                }
                if (key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
                {
                    return "\"" + key + "\" cannot be a payload key: Strybok reads utm_ keys itself";
                }
                if (Array.IndexOf(ReservedKeys, key) >= 0) return "\"" + key + "\" is reserved and cannot be a payload key";
                if (!seen.Add(key)) return "\"" + key + "\" is listed twice";
            }
            return null;
        }

        /// <summary>
        /// Whether a URL whose scheme, host, user info and port already passed is ours with the
        /// option; <paramref name="lookup"/> is then what is sent to resolve.
        /// </summary>
        /// <param name="host">The host, lower-cased.</param>
        /// <param name="path">The path exactly as received; may be empty.</param>
        /// <param name="rawQuery">The query without <c>?</c> and fragment; null or empty for none.</param>
        /// <param name="payloadKeys">The validated keys of the option.</param>
        /// <param name="lookup">The lookup, or null when the URL is not ours.</param>
        public static bool TryBuildLookup(string host, string path, string rawQuery, string[] payloadKeys, out string lookup)
        {
            lookup = null;
            // 1. A slug is ours, as without the option; the server answers it as a stored link or a
            //    path link. (Checked first: "/_strybok" is a slug, although "/_strybok/x" is reserved.)
            if (IsSlugPath(path))
            {
                lookup = PathLookup(host, path, rawQuery, payloadKeys);
                return true;
            }

            // 2. An empty path is the root.
            if (path.Length == 0) path = "/";

            // 3. A Firebase long link: the root with a "link" parameter. Ours only when the link is
            //    a deep link; it is never read as a route instead.
            if (path == "/")
            {
                bool found;
                string link;
                FindFirst(rawQuery, "link", out found, out link);
                if (found) return TryBuildLongLinkLookup(host, link, rawQuery, payloadKeys, out lookup);
            }

            // 4. A route path.
            if (!IsRoutePath(path)) return false;
            lookup = PathLookup(host, path, rawQuery, payloadKeys);
            return true;
        }

        /// <summary>
        /// Percent-decodes <c>text[start, end)</c> like the campaign query: <c>+</c> is a space,
        /// <c>%XX</c> a byte, the bytes UTF-8. Null when an escape is malformed or the bytes are not
        /// UTF-8 (overlong forms, surrogates and lone continuation bytes included). Characters that
        /// were not escaped stand for themselves.
        /// </summary>
        internal static string Decode(string text, int start, int end)
        {
            var bytes = new byte[(end - start) * 3];
            int count = 0;
            for (int i = start; i < end; i++)
            {
                char c = text[i];
                if (c == '%')
                {
                    if (i + 2 >= end) return null;
                    int high = HexValue(text[i + 1]);
                    int low = HexValue(text[i + 2]);
                    if (high < 0 || low < 0) return null;
                    bytes[count++] = (byte)(high * 16 + low);
                    i += 2;
                }
                else if (c == '+')
                {
                    bytes[count++] = (byte)' ';
                }
                else if (c < 0x80)
                {
                    bytes[count++] = (byte)c;
                }
                else if (c < 0x800)
                {
                    bytes[count++] = (byte)(0xC0 | (c >> 6));
                    bytes[count++] = (byte)(0x80 | (c & 0x3F));
                }
                else if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(text[i + 1]))
                {
                    int codePoint = char.ConvertToUtf32(c, text[i + 1]);
                    bytes[count++] = (byte)(0xF0 | (codePoint >> 18));
                    bytes[count++] = (byte)(0x80 | ((codePoint >> 12) & 0x3F));
                    bytes[count++] = (byte)(0x80 | ((codePoint >> 6) & 0x3F));
                    bytes[count++] = (byte)(0x80 | (codePoint & 0x3F));
                    i += 1;
                }
                else if (char.IsSurrogate(c))
                {
                    return null; // A lone surrogate is no character.
                }
                else
                {
                    bytes[count++] = (byte)(0xE0 | (c >> 12));
                    bytes[count++] = (byte)(0x80 | ((c >> 6) & 0x3F));
                    bytes[count++] = (byte)(0x80 | (c & 0x3F));
                }
            }
            return DecodeUtf8(bytes, count);
        }

        // ---------------------------------------------------------------------

        /// <summary><c>https://{host}{path}</c>, the payload query, then the campaign query.</summary>
        private static string PathLookup(string host, string path, string rawQuery, string[] payloadKeys)
        {
            var lookup = new StringBuilder(64 + path.Length);
            lookup.Append("https://").Append(host).Append(path);
            int pairs = AppendPayloadQuery(lookup, rawQuery, payloadKeys, 0);
            AppendCampaignQuery(lookup, rawQuery, pairs);
            return lookup.ToString();
        }

        /// <summary>
        /// <c>https://{host}/?link={deep}</c>, then <c>apn</c> and <c>ibi</c> when the tapped URL has
        /// them, then the campaign query. Nothing else of the tapped URL is sent.
        /// </summary>
        private static bool TryBuildLongLinkLookup(
            string host, string link, string rawQuery, string[] payloadKeys, out string lookup)
        {
            lookup = null;
            string deep;
            if (link == null || !TryRebuildDeepLink(link, payloadKeys, out deep)) return false;

            // Sent as they are, even empty: the server compares them with the app's identifiers and
            // refuses a mismatch, as the link host does. One that cannot be decoded is not ours.
            bool hasApn;
            string apn;
            FindFirst(rawQuery, "apn", out hasApn, out apn);
            if (hasApn && apn == null) return false;
            bool hasIbi;
            string ibi;
            FindFirst(rawQuery, "ibi", out hasIbi, out ibi);
            if (hasIbi && ibi == null) return false;

            var builder = new StringBuilder(64 + deep.Length * 3);
            builder.Append("https://").Append(host).Append("/?link=");
            AppendEncoded(builder, deep);
            if (hasApn) AppendEncoded(builder.Append("&apn="), apn);
            if (hasIbi) AppendEncoded(builder.Append("&ibi="), ibi);
            AppendCampaignQuery(builder, rawQuery, 1);
            lookup = builder.ToString();
            return true;
        }

        /// <summary>
        /// For a deep link (<c>http(s)://host</c> and an optional path, query and fragment):
        /// <c>{lower-cased scheme}://{lower-cased host}{path}</c>, <c>/</c> for an empty path,
        /// followed by the payload query of its own query. Its fragment and the rest of its query
        /// are dropped. False when <paramref name="value"/> is not a deep link.
        /// </summary>
        private static bool TryRebuildDeepLink(string value, string[] payloadKeys, out string deep)
        {
            deep = null;
            foreach (char c in value)
            {
                if (c <= ' ' || c == '\u007F' || c == '\\') return false;
            }

            string scheme;
            if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) scheme = "https";
            else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) scheme = "http";
            else return false;

            int hostStart = scheme.Length + 3;
            int hostEnd = hostStart;
            while (hostEnd < value.Length && IsDeepLinkHostChar(value[hostEnd])) hostEnd++;
            if (hostEnd == hostStart) return false;
            // ':' of a port, '@' of user info, '[' of an IP literal: not a deep link.
            if (hostEnd < value.Length && value[hostEnd] != '/' && value[hostEnd] != '?' && value[hostEnd] != '#')
            {
                return false;
            }

            int pathEnd = value.IndexOfAny(QueryOrFragment, hostEnd);
            if (pathEnd < 0) pathEnd = value.Length;
            string query = null;
            if (pathEnd < value.Length && value[pathEnd] == '?')
            {
                int queryEnd = value.IndexOf('#', pathEnd);
                if (queryEnd < 0) queryEnd = value.Length;
                query = value.Substring(pathEnd + 1, queryEnd - pathEnd - 1);
            }

            var builder = new StringBuilder(value.Length + 8);
            builder.Append(scheme).Append("://").Append(value.Substring(hostStart, hostEnd - hostStart).ToLowerInvariant());
            if (pathEnd == hostEnd) builder.Append('/');
            else builder.Append(value, hostEnd, pathEnd - hostEnd);
            AppendPayloadQuery(builder, query, payloadKeys, 0);
            deep = builder.ToString();
            return true;
        }

        /// <summary>
        /// Appends <c>key=value</c> for each payload key, in the configured order, whose first
        /// occurrence in <paramref name="rawQuery"/> has a usable value, each pair preceded by
        /// <c>?</c> when it is the first of the query and by <c>&amp;</c> otherwise. Returns the
        /// number of pairs the query has now.
        /// </summary>
        private static int AppendPayloadQuery(StringBuilder builder, string rawQuery, string[] payloadKeys, int pairs)
        {
            foreach (string key in payloadKeys)
            {
                bool found;
                string value;
                FindFirst(rawQuery, key, out found, out value);
                // A later occurrence never counts, even when the first one is dropped.
                if (!found || !IsPayloadValue(value)) continue;
                builder.Append(pairs == 0 ? '?' : '&').Append(key).Append('=');
                AppendEncoded(builder, value);
                pairs++;
            }
            return pairs;
        }

        /// <summary>The campaign pairs after <paramref name="pairs"/> pairs already in the query.</summary>
        private static void AppendCampaignQuery(StringBuilder builder, string rawQuery, int pairs)
        {
            string campaign = CampaignQuery.Build(rawQuery); // "" or "?key=value&…"
            if (campaign.Length == 0) return;
            if (pairs == 0) builder.Append(campaign);
            else builder.Append('&').Append(campaign, 1, campaign.Length - 1);
        }

        /// <summary>
        /// The first part of <paramref name="rawQuery"/> (split on <c>&amp;</c>, each part on its
        /// first <c>=</c>) whose decoded key is <paramref name="key"/>, compared with case.
        /// <paramref name="value"/> is its decoded value (empty for a part without <c>=</c>), or
        /// null when the value cannot be decoded. A key that cannot be decoded is no key of ours.
        /// </summary>
        private static void FindFirst(string rawQuery, string key, out bool found, out string value)
        {
            found = false;
            value = null;
            if (string.IsNullOrEmpty(rawQuery)) return;
            int start = 0;
            while (start <= rawQuery.Length)
            {
                int end = rawQuery.IndexOf('&', start);
                if (end < 0) end = rawQuery.Length;
                int equals = rawQuery.IndexOf('=', start, end - start);
                int keyEnd = equals < 0 ? end : equals;
                if (string.Equals(Decode(rawQuery, start, keyEnd), key, StringComparison.Ordinal))
                {
                    found = true;
                    value = equals < 0 ? string.Empty : Decode(rawQuery, equals + 1, end);
                    return;
                }
                start = end + 1;
            }
        }

        /// <summary>1–256 UTF-16 code units, none in U+0000–U+001F or U+007F–U+009F.</summary>
        private static bool IsPayloadValue(string value)
        {
            if (value == null || value.Length < 1 || value.Length > MaxPayloadValueLength) return false;
            foreach (char c in value)
            {
                if (c <= '\u001F' || (c >= '\u007F' && c <= '\u009F')) return false;
            }
            return true;
        }

        /// <summary>
        /// 1–512 characters starting with <c>/</c>; only route characters, every <c>%</c> followed by
        /// two hexadecimal digits; no <c>//</c>; no <c>.</c> or <c>..</c> segment (with
        /// <c>%2e</c> read as <c>.</c>); nothing reserved.
        /// </summary>
        private static bool IsRoutePath(string path)
        {
            if (path.Length < 1 || path.Length > MaxRoutePathLength || path[0] != '/') return false;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '%')
                {
                    if (i + 2 >= path.Length || HexValue(path[i + 1]) < 0 || HexValue(path[i + 2]) < 0) return false;
                }
                else if (!IsRoutePathChar(c))
                {
                    return false;
                }
                if (c == '/' && i + 1 < path.Length && path[i + 1] == '/') return false;
            }

            int segmentStart = 1;
            for (int i = 1; i <= path.Length; i++)
            {
                if (i < path.Length && path[i] != '/') continue;
                if (IsDotSegment(path, segmentStart, i)) return false;
                segmentStart = i + 1;
            }
            return !IsReserved(path);
        }

        /// <summary>Whether <c>path[start, end)</c> is <c>.</c> or <c>..</c>, each dot possibly written <c>%2e</c>.</summary>
        private static bool IsDotSegment(string path, int start, int end)
        {
            int dots = 0;
            int i = start;
            while (i < end)
            {
                if (path[i] == '.')
                {
                    i += 1;
                }
                else if (path[i] == '%' && i + 2 < end && path[i + 1] == '2' && (path[i + 2] == 'e' || path[i + 2] == 'E'))
                {
                    i += 3;
                }
                else
                {
                    return false;
                }
                dots++;
            }
            return dots == 1 || dots == 2;
        }

        private static bool IsReserved(string path)
        {
            foreach (string prefix in ReservedPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.Ordinal) &&
                    (path.Length == prefix.Length || path[prefix.Length] == '/'))
                {
                    return true;
                }
            }
            return Array.IndexOf(ReservedFiles, path) >= 0;
        }

        /// <summary><c>^/[A-Za-z0-9_-]{1,64}$</c>.</summary>
        private static bool IsSlugPath(string path)
        {
            if (path.Length < 2 || path.Length > MaxSlugLength + 1 || path[0] != '/') return false;
            for (int i = 1; i < path.Length; i++)
            {
                char c = path[i];
                if (!(IsAsciiLetterOrDigit(c) || c == '_' || c == '-')) return false;
            }
            return true;
        }

        private static bool IsKeyShape(string key)
        {
            if (key == null || key.Length < 1 || key.Length > MaxPayloadKeyLength) return false;
            foreach (char c in key)
            {
                if (!(IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) return false;
            }
            return true;
        }

        /// <summary><c>A–Z a–z 0–9 - . _ ~ ! $ &amp; ' ( ) * + , ; = : @</c> and <c>/</c>.</summary>
        private static bool IsRoutePathChar(char c)
        {
            if (IsAsciiLetterOrDigit(c)) return true;
            switch (c)
            {
                case '-':
                case '.':
                case '_':
                case '~':
                case '!':
                case '$':
                case '&':
                case '\'':
                case '(':
                case ')':
                case '*':
                case '+':
                case ',':
                case ';':
                case '=':
                case ':':
                case '@':
                case '/':
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsDeepLinkHostChar(char c)
        {
            return IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_';
        }

        private static bool IsAsciiLetterOrDigit(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        }

        /// <summary>The UTF-8 bytes of <paramref name="value"/>, each outside <c>A–Z a–z 0–9 - . _ ~</c> as <c>%XX</c>.</summary>
        private static void AppendEncoded(StringBuilder builder, string value)
        {
            foreach (byte b in Encoding.UTF8.GetBytes(value))
            {
                char c = (char)b;
                if (IsAsciiLetterOrDigit(c) || c == '-' || c == '.' || c == '_' || c == '~')
                {
                    builder.Append(c);
                }
                else
                {
                    builder.Append('%').Append(Hex[b >> 4]).Append(Hex[b & 0x0F]);
                }
            }
        }

        /// <summary>Strict UTF-8 (Unicode table 3-7); null for anything that is not well-formed.</summary>
        private static string DecodeUtf8(byte[] bytes, int count)
        {
            var text = new StringBuilder(count);
            int i = 0;
            while (i < count)
            {
                int lead = bytes[i];
                if (lead < 0x80)
                {
                    text.Append((char)lead);
                    i += 1;
                    continue;
                }

                int length;
                int codePoint;
                if (lead >= 0xC2 && lead <= 0xDF)
                {
                    length = 2;
                    codePoint = lead & 0x1F;
                }
                else if (lead >= 0xE0 && lead <= 0xEF)
                {
                    length = 3;
                    codePoint = lead & 0x0F;
                }
                else if (lead >= 0xF0 && lead <= 0xF4)
                {
                    length = 4;
                    codePoint = lead & 0x07;
                }
                else
                {
                    return null;
                }
                if (i + length > count) return null;
                for (int k = 1; k < length; k++)
                {
                    int next = bytes[i + k];
                    if ((next & 0xC0) != 0x80) return null;
                    codePoint = (codePoint << 6) | (next & 0x3F);
                }
                if ((length == 3 && codePoint < 0x800) || (length == 4 && codePoint < 0x10000) ||
                    (codePoint >= 0xD800 && codePoint <= 0xDFFF) || codePoint > 0x10FFFF)
                {
                    return null; // An overlong form, a surrogate or beyond Unicode.
                }
                if (codePoint < 0x10000) text.Append((char)codePoint);
                else text.Append(char.ConvertFromUtf32(codePoint));
                i += length;
            }
            return text.ToString();
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
