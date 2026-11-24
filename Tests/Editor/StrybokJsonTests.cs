using System.Collections.Generic;
using NUnit.Framework;

namespace Strybok.Tests
{
    public sealed class StrybokJsonTests
    {
        private static object Parse(string json)
        {
            object value;
            Assert.That(StrybokJson.TryParse(json, out value), Is.True, json);
            return value;
        }

        [Test]
        public void Parses_Scalars()
        {
            Assert.That(Parse("null"), Is.Null);
            Assert.That(Parse(" true "), Is.EqualTo(true));
            Assert.That(Parse("false"), Is.EqualTo(false));
            Assert.That(Parse("0"), Is.EqualTo(0L));
            Assert.That(Parse("-12"), Is.EqualTo(-12L));
            Assert.That(Parse("9223372036854775807"), Is.EqualTo(long.MaxValue));
            Assert.That(Parse("9223372036854775808"), Is.EqualTo(9223372036854775808d));
            Assert.That(Parse("1.5"), Is.EqualTo(1.5d));
            Assert.That(Parse("-2e3"), Is.EqualTo(-2000d));
            Assert.That(Parse("1E-2"), Is.EqualTo(0.01d));
            Assert.That(Parse("\"\""), Is.EqualTo(string.Empty));
            Assert.That(Parse("\"a\\\"b\\\\c\\/d\\n\\t\\r\\b\\f\\u0041\\u00e9\""), Is.EqualTo("a\"b\\c/d\n\t\r\b\fA\u00e9"));
            Assert.That(Parse("\"\\ud83d\\ude00\""), Is.EqualTo("\ud83d\ude00"));
        }

        [Test]
        public void Parses_ObjectsAndArrays()
        {
            var map = (Dictionary<string, object>)Parse(" { \"a\" : [ 1 , \"x\" , { } , [ ] ] , \"b\" : { \"c\" : null } , \"a2\":1, \"a2\":2 } ");
            var list = (List<object>)map["a"];
            Assert.That(list[0], Is.EqualTo(1L));
            Assert.That(list[1], Is.EqualTo("x"));
            Assert.That(list[2], Is.InstanceOf<Dictionary<string, object>>());
            Assert.That(list[3], Is.InstanceOf<List<object>>());
            Assert.That(((Dictionary<string, object>)map["b"])["c"], Is.Null);
            Assert.That(map["a2"], Is.EqualTo(2L), "a repeated key keeps its last value");
        }

        [Test]
        public void Rejects_InvalidJson()
        {
            var invalid = new[]
            {
                null, "", " ", "{", "}", "[", "[1,]", "{\"a\":1,}", "{a:1}", "{'a':1}", "{\"a\" 1}", "{\"a\":}",
                "tru", "nul", "True", "01", "1.", ".5", "-", "+1", "1e", "0x10", "NaN", "Infinity",
                "\"abc", "\"a\nb\"", "\"\\x\"", "\"\\u12\"", "\"\\u12g4\"", "1 2", "[1] x", "{} {}",
            };
            foreach (string json in invalid)
            {
                object value;
                Assert.That(StrybokJson.TryParse(json, out value), Is.False, json ?? "(null)");
                Assert.That(value, Is.Null);
            }
        }

        [Test]
        public void Rejects_ExcessiveNestingInsteadOfOverflowingTheStack()
        {
            object value;
            Assert.That(StrybokJson.TryParse(new string('[', 50) + new string(']', 50), out value), Is.True);
            Assert.That(StrybokJson.TryParse(new string('[', 100000), out value), Is.False);
            Assert.That(StrybokJson.TryParse(new string('[', 200) + new string(']', 200), out value), Is.False);
        }

        [Test]
        public void Serialize_RoundTrips()
        {
            var original = new Dictionary<string, object>
            {
                { "text", "line\nbreak \"quoted\" back\\slash \u0001 \u00e9" },
                { "int", 42L },
                { "negative", -7 },
                { "double", 0.1d },
                { "yes", true },
                { "nothing", null },
                { "list", new List<object> { 1L, "two", false, null } },
                { "map", new Dictionary<string, object> { { "k", "v" } } },
            };

            string json = StrybokJson.Serialize(original);
            var copy = (Dictionary<string, object>)Parse(json);

            Assert.That(copy["text"], Is.EqualTo(original["text"]));
            Assert.That(copy["int"], Is.EqualTo(42L));
            Assert.That(copy["negative"], Is.EqualTo(-7L));
            Assert.That(copy["double"], Is.EqualTo(0.1d));
            Assert.That(copy["yes"], Is.EqualTo(true));
            Assert.That(copy["nothing"], Is.Null);
            Assert.That((List<object>)copy["list"], Is.EqualTo(new object[] { 1L, "two", false, null }));
            Assert.That(((Dictionary<string, object>)copy["map"])["k"], Is.EqualTo("v"));
            Assert.That(json, Does.Not.Contain("\n"));
        }

        [Test]
        public void Serialize_UsesInvariantNumbersAndHandlesNonFiniteValues()
        {
            Assert.That(StrybokJson.Serialize(1.5d), Is.EqualTo("1.5"));
            Assert.That(StrybokJson.Serialize(2.5f), Is.EqualTo("2.5"));
            Assert.That(StrybokJson.Serialize(double.NaN), Is.EqualTo("null"));
            Assert.That(StrybokJson.Serialize(new Dictionary<string, object>()), Is.EqualTo("{}"));
            Assert.That(StrybokJson.Serialize(new List<object>()), Is.EqualTo("[]"));
            Assert.That(StrybokJson.Serialize(null), Is.EqualTo("null"));
        }
    }
}
