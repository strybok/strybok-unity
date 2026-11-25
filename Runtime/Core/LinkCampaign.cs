using System;

namespace Strybok
{
    /// <summary>
    /// Campaign values of an opened link — the usual <c>utm_source</c>, <c>utm_medium</c>,
    /// <c>utm_campaign</c>, <c>utm_term</c> and <c>utm_content</c> of web analytics. Each one
    /// is null when it has no value.
    ///
    /// They are the defaults the owner set on the link, overridden by the <c>utm_*</c>
    /// parameters of the URL that was tapped. Whoever shares a link can write any value into
    /// its URL, so treat them as labels (for your analytics, say), never as something to
    /// trust or to act on.
    /// </summary>
    public sealed class LinkCampaign : IEquatable<LinkCampaign>
    {
        /// <summary>No campaign values.</summary>
        public static readonly LinkCampaign Empty = new LinkCampaign();

        public LinkCampaign(
            string source = null,
            string medium = null,
            string campaign = null,
            string term = null,
            string content = null)
        {
            Source = source;
            Medium = medium;
            Campaign = campaign;
            Term = term;
            Content = content;
        }

        /// <summary><c>utm_source</c>, for example <c>instagram</c>; null when not set.</summary>
        public string Source { get; }

        /// <summary><c>utm_medium</c>, for example <c>social</c>; null when not set.</summary>
        public string Medium { get; }

        /// <summary><c>utm_campaign</c>, for example <c>spring_sale</c>; null when not set.</summary>
        public string Campaign { get; }

        /// <summary><c>utm_term</c>; null when not set.</summary>
        public string Term { get; }

        /// <summary><c>utm_content</c>; null when not set.</summary>
        public string Content { get; }

        /// <summary>True when the link has no campaign value at all.</summary>
        public bool IsEmpty
        {
            get { return Source == null && Medium == null && Campaign == null && Term == null && Content == null; }
        }

        public bool Equals(LinkCampaign other)
        {
            return other != null &&
                   string.Equals(Source, other.Source, StringComparison.Ordinal) &&
                   string.Equals(Medium, other.Medium, StringComparison.Ordinal) &&
                   string.Equals(Campaign, other.Campaign, StringComparison.Ordinal) &&
                   string.Equals(Term, other.Term, StringComparison.Ordinal) &&
                   string.Equals(Content, other.Content, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as LinkCampaign);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (Source == null ? 0 : StringComparer.Ordinal.GetHashCode(Source));
                hash = hash * 31 + (Medium == null ? 0 : StringComparer.Ordinal.GetHashCode(Medium));
                hash = hash * 31 + (Campaign == null ? 0 : StringComparer.Ordinal.GetHashCode(Campaign));
                hash = hash * 31 + (Term == null ? 0 : StringComparer.Ordinal.GetHashCode(Term));
                hash = hash * 31 + (Content == null ? 0 : StringComparer.Ordinal.GetHashCode(Content));
                return hash;
            }
        }

        public override string ToString()
        {
            return "LinkCampaign(source: " + Source + ", medium: " + Medium + ", campaign: " + Campaign +
                   ", term: " + Term + ", content: " + Content + ")";
        }
    }
}
