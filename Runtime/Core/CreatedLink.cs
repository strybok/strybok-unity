using System;

namespace Strybok
{
    /// <summary>
    /// A short link made by <see cref="StrybokClient.CreateLinkAsync"/>: hand
    /// <see cref="Url"/> to the share sheet.
    /// </summary>
    public sealed class CreatedLink
    {
        public CreatedLink(string url, string slug, string routePath, string linkId)
        {
            Url = url;
            Slug = slug;
            RoutePath = routePath;
            LinkId = linkId;
        }

        /// <summary>What the user shares, e.g. <c>https://my-app.strybok.link/Ab3xK9</c>. Always <c>https</c>.</summary>
        public string Url { get; }

        /// <summary><see cref="Url"/> as a <see cref="System.Uri"/>.</summary>
        public Uri Uri
        {
            get { return new Uri(Url, UriKind.Absolute); }
        }

        /// <summary>The generated last part of <see cref="Url"/>.</summary>
        public string Slug { get; }

        /// <summary>The in-app route the link opens, as stored by the service.</summary>
        public string RoutePath { get; }

        public string LinkId { get; }

        public override string ToString()
        {
            return "CreatedLink(" + Url + ", linkId: " + LinkId + ")";
        }
    }
}
