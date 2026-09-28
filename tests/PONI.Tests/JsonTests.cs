using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Poni.Core;
using Xunit;

namespace Poni.Tests
{
    public class JsonTests
    {
        [Fact]
        public void Parses_all_value_kinds()
        {
            var root = Json.AsObject(Json.Parse("{\"s\":\"a\\\"b\\\\c\\n\\u00e9\",\"i\":42,\"n\":-1.5e2,\"t\":true,\"f\":false,\"z\":null,\"a\":[1,\"x\"],\"o\":{}}"))!;
            Assert.Equal("a\"b\\c\né", Json.Get(root, "s"));
            Assert.Equal(42L, Json.Get(root, "i"));
            Assert.Equal(-150.0, Json.Get(root, "n"));
            Assert.Equal(true, Json.Get(root, "t"));
            Assert.Equal(false, Json.Get(root, "f"));
            Assert.Null(Json.Get(root, "z"));
            Assert.Equal(2, Json.AsList(Json.Get(root, "a")).Count);
            Assert.NotNull(Json.AsObject(Json.Get(root, "o")));
        }

        [Fact]
        public void Ignores_bom_and_lookups_are_case_insensitive()
        {
            var root = Json.AsObject(Json.Parse("﻿ { \"DNS\" : [\"1.1.1.1\"] }"))!;
            Assert.Single(Json.AsList(Json.Get(root, "Dns")));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\":1,}")]
        [InlineData("[1 2]")]
        [InlineData("{'a':1}")]
        [InlineData("{\"a\":01}")]
        [InlineData("tru")]
        [InlineData("{\"a\":1} x")]
        [InlineData("\"unterminated")]
        public void Rejects_invalid_json(string text)
        {
            Assert.Throws<FormatException>(() => Json.Parse(text));
        }

        [Fact]
        public void Rejects_absurd_nesting()
        {
            Assert.Throws<FormatException>(() => Json.Parse(new string('[', 100) + new string(']', 100)));
        }

        [Fact]
        public void Writer_round_trips_and_is_culture_independent()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR"); // decimal comma must not leak
            try
            {
                var obj = new JsonObject
                {
                    { "Name", "Café \"Bureau\"\t\\ 1" },
                    { "Number", 3.25 },
                    { "Int", 24 },
                    { "Flag", true },
                    { "Nothing", null },
                    { "List", new List<object?> { "1.1.1.1", "8.8.8.8" } },
                    { "Nested", new JsonObject { { "Empty", new List<object?>() } } },
                };
                var text = Json.Write(obj);
                Assert.Contains("\"Number\": 3.25", text);
                Assert.Contains("\"List\": [\"1.1.1.1\", \"8.8.8.8\"]", text);

                var back = Json.AsObject(Json.Parse(text))!;
                Assert.Equal("Café \"Bureau\"\t\\ 1", Json.Get(back, "Name"));
                Assert.Equal(3.25, Json.Get(back, "Number"));
                Assert.Equal(24, Json.AsInt(Json.Get(back, "Int")));
                Assert.Equal(true, Json.AsBool(Json.Get(back, "Flag")));
                Assert.Null(Json.Get(back, "Nothing"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData(24L, 24)]
        [InlineData(24.0, 24)]
        [InlineData("24", 24)]
        [InlineData(" 24 ", 24)]
        [InlineData("abc", null)]
        [InlineData(24.5, null)]
        [InlineData(null, null)]
        public void AsInt_is_tolerant_but_exact(object? value, int? expected)
        {
            Assert.Equal(expected, Json.AsInt(value));
        }
    }
}
