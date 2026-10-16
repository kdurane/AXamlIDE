using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace AXamlIDE.Helpers;

    internal static class Utils
    {
        private static readonly XmlReaderSettings SXmlSettings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
        };

        public static bool CheckAvaloniaRoot(string content)
        {
            return CheckAvaloniaRoot(XmlReader.Create(new StringReader(content), SXmlSettings));
        }

        private static bool CheckAvaloniaRoot(XmlReader reader)
        {
            try
            {
                while (!reader.IsStartElement())
                {
                    reader.Read();
                }
                if (!reader.MoveToFirstAttribute())
                    return false;
                do
                {
                    if (reader.Name != "xmlns") continue;
                    reader.ReadAttributeValue();
                    return reader.Value.ToLower(CultureInfo.InvariantCulture) == AvaloniaNamespace;

                } while (reader.MoveToNextAttribute());
                return false;
            }
            catch
            {
                return false;
            }
        }

        public const string AvaloniaNamespace = "https://github.com/avaloniaui";
        public const string Xaml2006Namespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        public static TValue GetOrCreate<TKey, TValue>(this Dictionary<TKey, TValue> dic, TKey key,
            Func<TKey, TValue> getter) where TKey : notnull
        {
            if (!dic.TryGetValue(key, out var rv))
                dic[key] = rv = getter(key);
            return rv;
        }

        public static TValue GetOrCreate<TKey, TValue>(this Dictionary<TKey, TValue> dic, TKey key) where TValue : new() where TKey : notnull
        {
            if (!dic.TryGetValue(key, out var rv))
                dic[key] = rv = new TValue();
            return rv;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dic, TKey key) where TKey : notnull
        {
            return CollectionExtensions.GetValueOrDefault(dic, key);
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dic, params TKey[] keys) where TKey : notnull
        {
            TValue? rv = default;
            var found = keys.Any(t => dic.TryGetValue(t, out rv));
            return found
                ? rv
                : default;
        }

        public static bool TryGetValue<TKey, TValue>(this IDictionary<TKey, TValue> dic, Func<TKey, bool> keyMatch, out TValue? value)
        {
            foreach (var kv in dic)
            {
                if (!keyMatch(kv.Key)) continue;
                value = kv.Value;
                return true;
            }
            value = default;
            return false;
        }

        public static T? FirstOrDefault<T>(this IEnumerable<T> source, Func<T, T, bool> predicate, T arg)
        {
            foreach (var item in source)
            {
                if (predicate(item, arg))
                {
                    return item;
                }
            }
            return default;
        }
    }