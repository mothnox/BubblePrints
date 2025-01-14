﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Design;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Design;

namespace BlueprintExplorer
{
    static class JsonExtensions
    {
        public static bool ContainsIgnoreCase(this string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        public static string ParseAsString(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return null;

            if (node.TryGetProperty("m_Key", out var strKey) && node.TryGetProperty("Shared", out var sharedString) && node.TryGetProperty("m_OwnerString", out _))
            {
                var key = strKey.GetString();
                if (key.Length == 0 && sharedString.ValueKind == JsonValueKind.Object && sharedString.TryGetProperty("stringkey", out var sharedKey))
                    key = sharedKey.GetString();

                if (key.Length > 0)
                {
                    if (BlueprintDB.Instance.Strings.TryGetValue(key, out var str))
                        return str;
                    else
                        return "<string-not-present>";
                }
                else
                {
                    return "<null-string>";
                }
            }
            return null;
        }

        private static Random rng = new();
        private static string[] christmas = { "🎄", "❄️", "🦌", "⛄", "🎅" };
        public static string Seasonal(this string str)
        {
            if (!SeasonalOverlay.InSeason)
                return str;

            if (SeasonalOverlay.NearChristmas)
            {
                var season = christmas;
                var index = Math.Abs(str.GetHashCode()) % season.Length;
                return $"{season[index]} {str} {season[index]}";
            }

            return str;
        }
        public static Guid Guid(this string str) => System.Guid.Parse(str);
        public static bool IsSimple(this JsonElement elem)
        {
            return !elem.IsContainer();
        }

        public static bool TryGetSimple(this JsonElement elem, out string str)
        {
            if (elem.IsContainer())
            {
                str = null;
                return false;
            }
            else
            {
                str = elem.GetRawText();
                return true;
            }
        }

        public static string ParseTypeString(this string str)
        {
            if (str == null)
                return str;
            return str.Substring(0, str.IndexOf(','));
        }
        public static string TypeString(this JsonElement elem)
        {
            return elem.Str("$type").ParseTypeString();
        }

        public static (string Guid, string Name, string FullName) NewTypeStr(this string raw, bool strict = true)
        {
            var comma = raw.IndexOf(',');
            var shortName = raw[(comma + 2)..];
            var guid = raw[0..comma];
            var db = BlueprintDB.Instance;

            if (db.GuidToFullTypeName.TryGetValue(guid, out var fullTypeName))
                return (guid, shortName, fullTypeName);

            if (strict)
                throw new Exception($"Cannot find type with that name: {shortName}");

            return (null, null, null);
        }

        public static (string Guid, string Name, string FullName) NewTypeStr(this JsonElement elem, bool strict = true)
        {
            if (elem.ValueKind == JsonValueKind.String)
                return elem.GetString().NewTypeStr();
            else if (elem.ValueKind == JsonValueKind.Object)
                return elem.Str("$type").NewTypeStr(strict);
            else
                throw new Exception("invalid type query??");
        }

        public static bool True(this JsonElement elem, string child)
        {
            return elem.TryGetProperty(child, out var ch) && ch.ValueKind == JsonValueKind.True;
        }
        public static string Str(this JsonElement elem, string child)
        {
            if (elem.ValueKind != JsonValueKind.Object)
                return null;
            if (!elem.TryGetProperty(child, out var childNode))
                return null;
            return childNode.GetString();
        }
        public static float Float(this JsonElement elem, string child)
        {
            if (elem.TryGetProperty(child, out var prop))
                return (float)prop.GetDouble();
            return 0;
        }
        public static int Int(this JsonElement elem, string child)
        {
            if (elem.TryGetProperty(child, out var prop))
                return prop.GetInt32();
            return 0;
        }

        public static bool IsContainer(this JsonElement elem)
        {
            return elem.ValueKind == JsonValueKind.Object || elem.ValueKind == JsonValueKind.Array;
        }
        public static bool IsEmptyContainer(this JsonElement elem)
        {
            return (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() == 0) || (elem.ValueKind == JsonValueKind.Object && !elem.EnumerateObject().Any());
        }

        public static void Visit(this JsonElement elem, Action<int, JsonElement> arrayIt, Action<string, JsonElement> objIt, Action<string> valIt, bool autoRecurse = false)
        {
            if (elem.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement entry in elem.EnumerateArray())
                {
                    arrayIt(index++, entry);
                    if (autoRecurse)
                        entry.Visit(arrayIt, objIt, valIt, true);
                }
            }
            else if (elem.ValueKind == JsonValueKind.Object && elem.EnumerateObject().Any())
            {
                foreach (var entry in elem.EnumerateObject())
                {
                    objIt(entry.Name, entry.Value);
                    if (autoRecurse)
                        entry.Value.Visit(arrayIt, objIt, valIt, true);
                }
            }
            else
            {
                valIt?.Invoke(elem.GetRawText());
            }
        }
    }

    public class BlueprintHandle : ISearchable
    {
        //public byte[] guid;
        public string GuidText { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string TypeName;
        public string Namespace;
        public string Raw { get; set; }
        public JsonElement obj;
        public bool Parsed;

        public List<Guid> BackReferences = new();

        public string NameLower;
        public string TypeNameLower;
        public string NamespaceLower;

        #region ISearchable
        //internal Dictionary<string, Func<string>> _Providers = null;
        //public Dictionary<string, Func<string>> Providers { get {
        //        if (_Providers != null) return _Providers;
        //        _Providers = new() {
        //            { "name", () => this.NameLower },
        //            { "type", () => this.TypeNameLower },
        //            { "space", () => this.NamespaceLower }
        //        };
        //        return _Providers;
        //   }
        //}
        internal MatchResult[][] _Matches;
        public ushort[] ComponentIndex;
        public IEnumerable<string> ComponentsList => ComponentIndex.Select(i => BlueprintDB.Instance.ComponentTypeLookup[i]);
        internal static readonly MatchQuery.MatchProvider MatchProvider = new(
                    obj => (obj as BlueprintHandle).NameLower,
                    obj => (obj as BlueprintHandle).TypeNameLower,
                    obj => (obj as BlueprintHandle).NamespaceLower,
                    obj => (obj as BlueprintHandle).GuidText);

        private MatchResult[] CreateResultArray()
        {
            return new MatchResult[] {
                    new MatchResult("name", this),
                    new MatchResult("type", this),
                    new MatchResult("space", this),
                    new MatchResult("guid", this),
                };
        }
        public MatchResult[] GetMatches(int index)
        {
            if (_Matches[index] == null)
                _Matches[index] = CreateResultArray();
            return _Matches[index];
        }

        internal JsonElement EnsureObj
        {
            get
            {
                EnsureParsed();
                return obj;
            }
        }


        internal void EnsureParsed()
        {
            if (!Parsed)
            {
                obj = JsonSerializer.Deserialize<JsonElement>(Raw);
                Parsed = true;
            }
        }


        #endregion
        public void PrimeMatches(int count)
        {
            _Matches = new MatchResult[count][];
            for (int i = 0; i < count; i++)
                _Matches[i] = CreateResultArray();
        }

        public class ElementVisitor
        {

            public static IEnumerable<(VisitedElement, string)> Visit(BlueprintHandle bp)
            {
                Stack<string> stack = new();
                foreach (var elem in BlueprintHandle.Visit(bp.EnsureObj, bp.Name))
                {
                    if (elem.levelDelta > 0)
                    {
                        stack.Push(elem.key);
                        yield return (elem, string.Join("/", stack.Reverse()));
                    }
                    else if (elem.levelDelta < 0)
                        stack.Pop();
                    else
                        yield return (elem, string.Join("/", stack.Reverse()));

                }
            }

        }

        public class VisitedElement
        {
            public string key;
            public string value;
            public int levelDelta;
            public bool isObj;
            public string link;
            //public string linkTarget;
            public bool Empty;
            public JsonElement Node;
            public bool Last;
        }

        public static string ParseReference(string val)
        {
            if (val.StartsWith("!bp_"))
            {
                return val[4..];
            }
            else if (val.StartsWith("Blueprint:"))
            {
                var components = val.Split(':');
                if (components.Length != 3 || components[1].Length == 0 || components[1] == "NULL")
                {
                    return null;
                }
                return components[1];
            }
            else
            {
                return null;
            }

        }

        public IEnumerable<VisitedElement> Elements
        {
            get
            {
                EnsureParsed();
                return Visit(obj, Name);
            }
        }

        public IEnumerable<string> Objects
        {
            get
            {
                EnsureParsed();
                return VisitObjects(obj);
            }
        }

        public static IEnumerable<string> VisitObjects(JsonElement node, string context = null)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var elem in node.EnumerateArray())
                {
                    foreach (var n in VisitObjects(elem, context + "/" + index.ToString()))
                        yield return n;
                    index++;
                }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("$type", out var _))
                    yield return node.NewTypeStr().Guid;
                foreach (var elem in node.EnumerateObject())
                {
                    foreach (var n in VisitObjects(elem.Value, context + "/" + elem.Name))
                        yield return n;
                }
            }

        }

        public static IEnumerable<VisitedElement> Visit(JsonElement node, string name)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                string val = node.GetString();
                var link = ParseReference(val);
                yield return new VisitedElement { key = name, value = val, link = link };
            }
            else if (node.ValueKind == JsonValueKind.Number || node.ValueKind == JsonValueKind.True || node.ValueKind == JsonValueKind.False)
            {
                yield return new VisitedElement { key = name, value = node.GetRawText() };
            }
            else if (node.ValueKind == JsonValueKind.Null)
            {
                yield return new VisitedElement { key = name, value = "null" };
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                yield return new VisitedElement { key = name, levelDelta = 1, Node = node };
                int index = 0;
                foreach (var elem in node.EnumerateArray())
                {
                    foreach (var n in Visit(elem, index.ToString()))
                        yield return n;
                    index++;
                }
                yield return new VisitedElement { levelDelta = -1 };
            }
            else
            {
                yield return new VisitedElement { key = name, levelDelta = 1, isObj = true, Node = node };
                foreach (var elem in node.EnumerateObject())
                {
                    foreach (var n in Visit(elem.Value, elem.Name))
                        yield return n;
                }
                yield return new VisitedElement { levelDelta = -1 };
            }

        }

        private static void GatherBlueprints(string path, List<BlueprintReference> refs, JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                var val = node.GetString();
                if (val.StartsWith("Blueprint:"))
                {
                    var components = val.Split(':');
                    if (components.Length != 3 || components[1].Length == 0 || components[1] == "NULL")
                    {
                        return;
                    }
                    var guid = Guid.Parse(components[1]);
                    refs.Add(new BlueprintReference
                    {
                        path = path,
                        to = guid
                    });
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var element in node.EnumerateArray())
                {
                    GatherBlueprints(path + "/" + index, refs, element);
                    index++;
                }
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in node.EnumerateObject())
                {
                    GatherBlueprints(path + "/" + prop.Name, refs, prop.Value);
                }
            }
        }


        public IEnumerable<Guid> GetDirectReferences()
        {
            Stack<string> path = new();
            foreach (var element in Elements)
            {
                //if (element.levelDelta > 0)
                //{
                //    path.Push(element.key);
                //}
                //else if (element.levelDelta < 0)
                //{
                //    path.Pop();
                //}
                //else
                //{
                if (element.link != null)
                {
                    yield return Guid.Parse(element.link);
                    //yield return new BlueprintReference
                    //{
                    //    path = string.Join("/", path.Reverse()),
                    //    to = Guid.Parse(element.link)
                    //};
                }
                //}
            }
        }

        internal void ParseType()
        {

            var components = Type.Split('.');
            if (components.Length <= 1)
                TypeName = Type;
            else
            {
                TypeName = components.Last();
                Namespace = string.Join('.', components.Take(components.Length - 1));
            }
        }
    }

    public class BlueprintReference
    {
        public string path;
        public Guid to;

    }
}
