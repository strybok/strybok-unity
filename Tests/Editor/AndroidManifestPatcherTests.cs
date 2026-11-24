using System.Xml;
using NUnit.Framework;
using Strybok.Editor;

namespace Strybok.Tests
{
    public sealed class AndroidManifestPatcherTests
    {
        private const string Android = "http://schemas.android.com/apk/res/android";

        private const string UnityManifest =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\" xmlns:tools=\"http://schemas.android.com/tools\">\n" +
            "  <application android:extractNativeLibs=\"true\">\n" +
            "    <meta-data android:name=\"unity.splash-mode\" android:value=\"0\" />\n" +
            "    <activity android:name=\"com.unity3d.player.UnityPlayerGameActivity\" android:exported=\"true\">\n" +
            "      <intent-filter>\n" +
            "        <category android:name=\"android.intent.category.LAUNCHER\" />\n" +
            "        <action android:name=\"android.intent.action.MAIN\" />\n" +
            "      </intent-filter>\n" +
            "      <meta-data android:name=\"unityplayer.UnityActivity\" android:value=\"true\" />\n" +
            "    </activity>\n" +
            "    <activity android:name=\"com.example.OtherActivity\" />\n" +
            "  </application>\n" +
            "</manifest>\n";

        private static XmlNodeList Filters(string manifest, string activityName)
        {
            var document = new XmlDocument();
            document.LoadXml(manifest);
            var namespaces = new XmlNamespaceManager(document.NameTable);
            namespaces.AddNamespace("android", Android);
            return document.SelectNodes(
                "/manifest/application/activity[@android:name='" + activityName + "']" +
                "/intent-filter[@android:autoVerify='true']",
                namespaces);
        }

        [Test]
        public void AddsAVerifiedHttpsFilterPerHostToTheLauncherActivityOnly()
        {
            int added;
            int launchers;
            string patched = AndroidManifestPatcher.Patch(
                UnityManifest, new[] { "my-app.strybok.link", "links.example.com" }, out added, out launchers);

            Assert.That(launchers, Is.EqualTo(1));
            Assert.That(added, Is.EqualTo(2));
            XmlNodeList filters = Filters(patched, "com.unity3d.player.UnityPlayerGameActivity");
            Assert.That(filters.Count, Is.EqualTo(2));
            Assert.That(Filters(patched, "com.example.OtherActivity").Count, Is.EqualTo(0));

            var filter = (XmlElement)filters[0];
            Assert.That(
                filter.OuterXml,
                Is.EqualTo(
                    "<intent-filter android:autoVerify=\"true\" xmlns:android=\"" + Android + "\">" +
                    "<action android:name=\"android.intent.action.VIEW\" />" +
                    "<category android:name=\"android.intent.category.DEFAULT\" />" +
                    "<category android:name=\"android.intent.category.BROWSABLE\" />" +
                    "<data android:scheme=\"https\" android:host=\"my-app.strybok.link\" />" +
                    "</intent-filter>"));
            Assert.That(patched, Does.Contain("unity.splash-mode"), "existing content is kept");
            Assert.That(patched, Does.Contain("xmlns:tools"));
        }

        [Test]
        public void PatchingTwiceChangesNothing()
        {
            int added;
            int launchers;
            string once = AndroidManifestPatcher.Patch(UnityManifest, new[] { "my-app.strybok.link" }, out added, out launchers);
            string twice = AndroidManifestPatcher.Patch(once, new[] { "MY-APP.strybok.link" }, out added, out launchers);

            Assert.That(added, Is.EqualTo(0));
            Assert.That(launchers, Is.EqualTo(1));
            Assert.That(twice, Is.EqualTo(once));
        }

        [Test]
        public void ReportsWhenThereIsNoLauncherActivity()
        {
            const string manifest =
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">" +
                "<application><activity android:name=\"a.B\" /></application></manifest>";
            int added;
            int launchers;
            string patched = AndroidManifestPatcher.Patch(manifest, new[] { "my-app.strybok.link" }, out added, out launchers);

            Assert.That(launchers, Is.EqualTo(0));
            Assert.That(added, Is.EqualTo(0));
            Assert.That(patched, Is.EqualTo(manifest));
        }
    }
}
