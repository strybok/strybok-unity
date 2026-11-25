using System;
using System.Text;

namespace Strybok
{
    /// <summary>
    /// Finds the click id of a deferred link in a store's install referrer and in the query of
    /// a pasted link (SDK contract, "Deferred links"). Never throws, and never keeps or logs
    /// its input.
    /// </summary>
    internal static class DeferredLinkParsing
    {
        private const string Key = "strybok_click";
        private const string EncodedKey = "strybok_click%3D";
        private const string IdPrefix = "dc_";
        private const int IdLength = 35;

        /// <summary><c>^dc_[0-9A-Za-z]{32}$</c>.</summary>
        public static bool IsClickId(string value)
        {
            if (value == null || value.Length != IdLength || !value.StartsWith(IdPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            for (int i = IdPrefix.Length; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// The click id in an install referrer, or null. The first part whose key is exactly
        /// <c>strybok_click</c> decides. Only when no part has that key and the string contains
        /// <c>strybok_click%3D</c> is the whole string percent-decoded once and searched again.
        /// </summary>
        public static string FindClickIdInReferrer(string referrer)
        {
            if (string.IsNullOrEmpty(referrer)) return null;
            string id;
            if (TryFindRaw(referrer, out id)) return id;
            if (referrer.IndexOf(EncodedKey, StringComparison.OrdinalIgnoreCase) < 0) return null;

            string decoded = PercentDecode(referrer, 0, referrer.Length, false);
            if (decoded == null) return null;
            TryFindRaw(decoded, out id);
            return id;
        }

        /// <summary>
        /// The click id of a pasted link that already passed "Which URLs are ours", or null
        /// when its first <c>strybok_click</c> query parameter is missing or not an id.
        /// </summary>
        public static string FindClickIdInLinkQuery(string url)
        {
            if (url == null) return null;
            int queryStart = url.IndexOf('?');
            int fragment = url.IndexOf('#');
            if (queryStart < 0 || (fragment >= 0 && fragment < queryStart)) return null;
            int end = fragment < 0 ? url.Length : fragment;

            int start = queryStart + 1;
            while (start <= end)
            {
                int partEnd = url.IndexOf('&', start, end - start);
                if (partEnd < 0) partEnd = end;
                int equals = url.IndexOf('=', start, partEnd - start);
                int keyEnd = equals < 0 ? partEnd : equals;

                if (string.Equals(PercentDecode(url, start, keyEnd, true), Key, StringComparison.Ordinal))
                {
                    // The first occurrence decides, even when its value is not an id.
                    string value = equals < 0 ? null : PercentDecode(url, equals + 1, partEnd, true);
                    return IsClickId(value) ? value : null;
                }
                start = partEnd + 1;
            }
            return null;
        }

        /// <summary>Returns true when a part has the key; <paramref name="id"/> is then its value if valid.</summary>
        private static bool TryFindRaw(string text, out string id)
        {
            id = null;
            int start = 0;
            while (start <= text.Length)
            {
                int end = text.IndexOf('&', start);
                if (end < 0) end = text.Length;
                int equals = text.IndexOf('=', start, end - start);
                int keyEnd = equals < 0 ? end : equals;
                if (keyEnd - start == Key.Length && string.CompareOrdinal(text, start, Key, 0, Key.Length) == 0)
                {
                    string value = equals < 0 ? null : text.Substring(equals + 1, end - equals - 1);
                    if (IsClickId(value)) id = value;
                    return true;
                }
                start = end + 1;
            }
            return false;
        }

        /// <summary>Strict percent-decoding as UTF-8; null for a malformed escape.</summary>
        private static string PercentDecode(string text, int start, int end, bool plusIsSpace)
        {
            var bytes = new byte[end - start];
            int count = 0;
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
                    bytes[count++] = (byte)(high * 16 + low);
                    i += 3;
                }
                else if (c > 0x7F)
                {
                    // Not ASCII: cannot be part of the key or of an id. Keep the position occupied.
                    bytes[count++] = (byte)'?';
                    i += 1;
                }
                else
                {
                    bytes[count++] = plusIsSpace && c == '+' ? (byte)' ' : (byte)c;
                    i += 1;
                }
            }
            return Encoding.UTF8.GetString(bytes, 0, count);
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
