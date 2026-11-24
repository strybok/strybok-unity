using System.Collections.Generic;
using System.Xml;

namespace Strybok.Editor
{
    /// <summary>
    /// Adds the verified App Links intent filter to an Android manifest. Pure text in,
    /// text out, so it can be unit-tested without building a player.
    /// </summary>
    public static class AndroidManifestPatcher
    {
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
        private const string ActionMain = "android.intent.action.MAIN";
        private const string ActionView = "android.intent.action.VIEW";
        private const string CategoryLauncher = "android.intent.category.LAUNCHER";
        private const string CategoryDefault = "android.intent.category.DEFAULT";
        private const string CategoryBrowsable = "android.intent.category.BROWSABLE";

        /// <summary>
        /// Returns <paramref name="manifestXml"/> with one
        /// <c>&lt;intent-filter android:autoVerify="true"&gt;</c> per host on every launcher
        /// activity. Hosts that an activity already declares for <c>https</c> are skipped,
        /// so patching twice changes nothing.
        /// </summary>
        /// <param name="addedFilters">Number of intent filters that were added.</param>
        /// <param name="launcherActivities">Number of launcher activities found; 0 means nothing could be patched.</param>
        public static string Patch(
            string manifestXml,
            IEnumerable<string> hosts,
            out int addedFilters,
            out int launcherActivities)
        {
            addedFilters = 0;
            launcherActivities = 0;

            var document = new XmlDocument { PreserveWhitespace = false };
            document.LoadXml(manifestXml);
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("android", AndroidNamespace);

            XmlNodeList activities = document.SelectNodes(
                "/manifest/application/activity[intent-filter[" +
                "action/@android:name='" + ActionMain + "' and " +
                "category/@android:name='" + CategoryLauncher + "']]",
                namespaces);
            if (activities == null) return manifestXml;

            foreach (XmlNode activity in activities)
            {
                launcherActivities++;
                foreach (string host in hosts)
                {
                    if (string.IsNullOrEmpty(host) || DeclaresHost(activity, host, namespaces)) continue;
                    activity.AppendChild(CreateFilter(document, host));
                    addedFilters++;
                }
            }
            if (addedFilters == 0) return manifestXml;

            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = false };
            using (var text = new Utf8StringWriter())
            {
                using (XmlWriter writer = XmlWriter.Create(text, settings))
                {
                    document.Save(writer);
                }
                return text.ToString();
            }
        }

        private static bool DeclaresHost(XmlNode activity, string host, XmlNamespaceManager namespaces)
        {
            XmlNodeList filters = activity.SelectNodes(
                "intent-filter[action/@android:name='" + ActionView + "']", namespaces);
            if (filters == null) return false;
            foreach (XmlNode filter in filters)
            {
                bool https = false;
                bool sameHost = false;
                foreach (XmlNode child in filter.ChildNodes)
                {
                    if (child.NodeType != XmlNodeType.Element || child.Name != "data") continue;
                    var data = (XmlElement)child;
                    if (data.GetAttribute("scheme", AndroidNamespace) == "https") https = true;
                    if (string.Equals(
                            data.GetAttribute("host", AndroidNamespace), host,
                            System.StringComparison.OrdinalIgnoreCase))
                    {
                        sameHost = true;
                    }
                }
                if (https && sameHost) return true;
            }
            return false;
        }

        private static XmlElement CreateFilter(XmlDocument document, string host)
        {
            XmlElement filter = document.CreateElement("intent-filter");
            filter.SetAttribute("autoVerify", AndroidNamespace, "true");
            filter.AppendChild(Named(document, "action", ActionView));
            filter.AppendChild(Named(document, "category", CategoryDefault));
            filter.AppendChild(Named(document, "category", CategoryBrowsable));
            XmlElement data = document.CreateElement("data");
            data.SetAttribute("scheme", AndroidNamespace, "https");
            data.SetAttribute("host", AndroidNamespace, host);
            filter.AppendChild(data);
            return filter;
        }

        private static XmlElement Named(XmlDocument document, string element, string name)
        {
            XmlElement node = document.CreateElement(element);
            node.SetAttribute("name", AndroidNamespace, name);
            return node;
        }

        private sealed class Utf8StringWriter : System.IO.StringWriter
        {
            public override System.Text.Encoding Encoding
            {
                get { return System.Text.Encoding.UTF8; }
            }
        }
    }
}
