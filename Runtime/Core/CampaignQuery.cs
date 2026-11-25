using System.Text;

namespace Strybok
{
    /// <summary>
    /// Builds the campaign query of the lookup URL (SDK contract, "Campaign query").
    ///
    /// Written by hand on purpose: <c>System.Uri</c> has no query parser, and the rule is
    /// strict enough that no general decoder is needed. Never throws.
    /// </summary>
    internal static class CampaignQuery
    {
        private const int MaxValueLength = 64;

        private static readonly string[] Keys =
        {
            "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        };

        /// <summary>
        /// Returns an empty string, or <c>?</c> followed by those of the five <c>utm_*</c>
        /// parameters of <paramref name="rawQuery"/> (a query string without <c>?</c> and
        /// without the fragment) whose first occurrence has a valid value, in a fixed order.
        /// Every other parameter is dropped: it may hold private data.
        /// </summary>
        public static string Build(string rawQuery)
        {
            if (string.IsNullOrEmpty(rawQuery)) return string.Empty;

            var values = new string[Keys.Length];
            var seen = new bool[Keys.Length];
            int start = 0;
            while (start <= rawQuery.Length)
            {
                int end = rawQuery.IndexOf('&', start);
                if (end < 0) end = rawQuery.Length;
                int equals = rawQuery.IndexOf('=', start, end - start);
                int keyEnd = equals < 0 ? end : equals;

                string key = DecodeUnreserved(rawQuery, start, keyEnd);
                int index = key == null ? -1 : System.Array.IndexOf(Keys, key);
                if (index >= 0 && !seen[index])
                {
                    seen[index] = true; // The first occurrence counts, even when its value is dropped.
                    string value = equals < 0 ? null : DecodeUnreserved(rawQuery, equals + 1, end);
                    if (value != null && value.Length >= 1 && value.Length <= MaxValueLength) values[index] = value;
                }
                start = end + 1;
            }

            StringBuilder query = null;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (values[i] == null) continue;
                if (query == null) query = new StringBuilder("?");
                else query.Append('&');
                query.Append(Keys[i]).Append('=').Append(values[i]);
            }
            return query == null ? string.Empty : query.ToString();
        }

        /// <summary>
        /// Percent-decodes a part of <paramref name="text"/> (<c>+</c> is a space) and returns
        /// the result only when it consists of <c>A–Z a–z 0–9 . _ ~ -</c>; null otherwise.
        ///
        /// Campaign keys and valid values are made of these characters only, so anything else —
        /// a space, a malformed escape, any byte of a multi-byte UTF-8 sequence (valid or not) —
        /// can only belong to a key that is not ours or to a value that is dropped. No UTF-8
        /// decoding is needed for that.
        /// </summary>
        private static string DecodeUnreserved(string text, int start, int end)
        {
            var decoded = new StringBuilder(end - start);
            int i = start;
            while (i < end)
            {
                char c = text[i];
                if (c == '%')
                {
                    if (i + 2 >= end) return null;
                    int high = HexValue(text[i + 1]);
                    int low = HexValue(text[i + 2]);
                    if (high < 0 || low < 0) return null;
                    c = (char)(high * 16 + low);
                    i += 3;
                }
                else
                {
                    i += 1;
                }
                if (!IsUnreserved(c)) return null;
                decoded.Append(c);
            }
            return decoded.ToString();
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private static bool IsUnreserved(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                   c == '.' || c == '_' || c == '~' || c == '-';
        }
    }
}
