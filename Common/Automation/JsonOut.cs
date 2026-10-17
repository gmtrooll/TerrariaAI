using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Converts command and snippet results to JSON without walking into game objects: a Player or NPC would
    /// serialize to megabytes. Plain data (anonymous types, tuples, dictionaries, lists, snippet-declared types)
    /// is expanded; any other object becomes its ToString(). Snippets project what they need, e.g.
    /// <c>return new { p.Center, p.statLife };</c>
    /// </summary>
    public static class JsonOut
    {
        private const int MaxDepth = 6;
        private const int MaxItems = 2000;

        public static JToken ToToken(object value, int depth = 0) {
            switch (value) {
                case null:
                    return JValue.CreateNull();
                case JToken token:
                    return token;
                case string s:
                    return new JValue(s);
                case bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                    return new JValue(value);
                case float f:
                    return float.IsFinite(f) ? new JValue(f) : new JValue(f.ToString());
                case double d:
                    return double.IsFinite(d) ? new JValue(d) : new JValue(d.ToString());
                case Enum e:
                    return new JValue(e.ToString());
                case Vector2 v:
                    return new JObject { ["x"] = v.X, ["y"] = v.Y };
                case Point p:
                    return new JObject { ["x"] = p.X, ["y"] = p.Y };
                case Rectangle r:
                    return new JObject { ["x"] = r.X, ["y"] = r.Y, ["w"] = r.Width, ["h"] = r.Height };
            }

            if (depth >= MaxDepth) return new JValue(value.ToString());

            if (value is IDictionary dictionary) {
                var obj = new JObject();
                foreach (DictionaryEntry entry in dictionary) {
                    if (obj.Count >= MaxItems) break;
                    obj[Convert.ToString(entry.Key)] = ToToken(entry.Value, depth + 1);
                }
                return obj;
            }

            if (value is IEnumerable sequence) {
                var array = new JArray();
                foreach (object item in sequence) {
                    if (array.Count >= MaxItems) {
                        array.Add("... truncated");
                        break;
                    }
                    array.Add(ToToken(item, depth + 1));
                }
                return array;
            }

            Type type = value.GetType();
            if (!IsPlainData(type)) return new JValue(value.ToString());

            var members = new JObject();
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (property.GetIndexParameters().Length == 0) members[property.Name] = ToToken(property.GetValue(value), depth + 1);
            }
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                members[field.Name] = ToToken(field.GetValue(value), depth + 1);
            }
            return members;
        }

        private static bool IsPlainData(Type type) {
            return type.IsDefined(typeof(CompilerGeneratedAttribute), false) // anonymous types
                || type.FullName?.StartsWith("System.ValueTuple") == true
                || type.Namespace == typeof(JsonOut).Namespace
                || type.Assembly.GetName().Name?.StartsWith(CSharpRunner.AssemblyPrefix) == true;
        }
    }
}
