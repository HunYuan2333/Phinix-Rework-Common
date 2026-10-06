using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Utils.Framework.ManagedExtensions
{
    public sealed class ManagedExtensionValidationException : Exception
    {
        public string ResourcePath { get; internal set; }
        public ManagedExtensionAssemblyReferenceFailure ReferenceFailure { get; internal set; }
        public ManagedExtensionValidationException(string code) : base(code) { Code = code; }
        public ManagedExtensionValidationException(string code, Exception inner) : base(code, inner) { Code = code; }
        public string Code { get; }
    }

    // Shared static parsing only: no networking, reflection loading or game APIs.
    internal static class ManagedExtensionJson
    {
        internal static XElement Read(byte[] bytes, int maximum)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > maximum) throw Error("DocumentLimit");
            bytes = (byte[])bytes.Clone();
            try
            {
                new UTF8Encoding(false, true).GetString(bytes);
                Boundary(bytes);
                var quotas = new XmlDictionaryReaderQuotas
                {
                    MaxDepth = 32, MaxStringContentLength = maximum, MaxArrayLength = 4096,
                    MaxBytesPerRead = 4096, MaxNameTableCharCount = 16384
                };
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, 0, bytes.Length,
                    new UTF8Encoding(false, true), quotas, null))
                {
                    XElement root = XElement.Load(reader);
                    if (reader.MoveToContent() != XmlNodeType.None) throw Error("InvalidJson");
                    if (root.DescendantsAndSelf().Any(n => n.Name.NamespaceName.Length != 0 ||
                        n.Attributes().Any(a => a.Name != "type"))) throw Error("InvalidJson");
                    return root;
                }
            }
            catch (XmlException ex) { throw new ManagedExtensionValidationException("InvalidJson", ex); }
            catch (DecoderFallbackException ex) { throw new ManagedExtensionValidationException("InvalidJson", ex); }
        }

        private static void Boundary(byte[] bytes)
        {
            int start = 0;
            while (start < bytes.Length && Space(bytes[start])) start++;
            if (start == bytes.Length || bytes[start] != '{') throw Error("InvalidJson");
            var stack = new Stack<byte>();
            bool quoted = false, escaped = false;
            for (int i = start; i < bytes.Length; i++)
            {
                byte c = bytes[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    else if (c < 32) throw Error("InvalidJson");
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{' || c == '[')
                {
                    stack.Push(c);
                    if (stack.Count > 32) throw Error("InvalidJson");
                }
                else if (c == '}' || c == ']')
                {
                    int previous = i - 1;
                    while (previous >= start && Space(bytes[previous])) previous--;
                    if (bytes[previous] == ',' || stack.Count == 0 ||
                        stack.Pop() != (c == '}' ? '{' : '[')) throw Error("InvalidJson");
                    if (stack.Count != 0) continue;
                    for (int j = i + 1; j < bytes.Length; j++) if (!Space(bytes[j])) throw Error("InvalidJson");
                    return;
                }
                else if (c == '/' || c == '\'') throw Error("InvalidJson");
            }
            throw Error("InvalidJson");
        }

        private static bool Space(byte c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        internal static Dictionary<string, XElement> Object(XElement node, params string[] allowed)
        {
            Type(node, "object");
            var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (XElement field in node.Elements())
            {
                if (!allowed.Contains(field.Name.LocalName)) throw Error("UnknownField");
                if (result.ContainsKey(field.Name.LocalName)) throw Error("DuplicateField");
                result.Add(field.Name.LocalName, field);
            }
            return result;
        }

        internal static XElement Required(Dictionary<string, XElement> fields, string name)
        {
            XElement result;
            if (!fields.TryGetValue(name, out result)) throw Error("MissingField");
            return result;
        }

        internal static List<XElement> Array(XElement node, int maximum)
        {
            Type(node, "array");
            var values = node.Elements().ToList();
            if (values.Count > maximum || values.Any(n => n.Name.LocalName != "item")) throw Error("CollectionLimit");
            return values;
        }

        internal static void Type(XElement node, string type)
        {
            if ((string)node.Attribute("type") != type) throw Error("WrongType");
        }

        internal static string Text(XElement node, int maximum)
        {
            Type(node, "string");
            string value = node.Value;
            if (value.Length == 0 || value.Length > maximum || value.Trim() != value || value.Any(char.IsControl)) throw Error("InvalidText");
            return value;
        }

        internal static string Id(XElement node) { return Identifier(Text(node, 128)); }

        internal static string Identifier(string value)
        {
            if (value == null || value.Length > 128 || !Regex.IsMatch(value, @"\A[a-z0-9]+(?:[._-][a-z0-9]+)*\z", RegexOptions.CultureInvariant))
                throw Error("InvalidId");
            return value;
        }

        internal static string Hex(XElement node, int length) { return Hex(Text(node, length), length); }

        internal static string Hex(string value, int length)
        {
            if (value == null || value.Length != length || value.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw Error("InvalidDigest");
            return value;
        }

        internal static long Integer(XElement node, long minimum, long maximum)
        {
            Type(node, "number");
            long value;
            if (!long.TryParse(node.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value) ||
                value < minimum || value > maximum || value.ToString(CultureInfo.InvariantCulture) != node.Value) throw Error("InvalidNumber");
            return value;
        }

        internal static bool Boolean(XElement node)
        {
            Type(node, "boolean");
            if (node.Value != "true" && node.Value != "false") throw Error("WrongType");
            return node.Value == "true";
        }

        internal static string Path(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 240 || value.Contains("\\")) throw Error("InvalidPath");
            foreach (string part in value.Split('/'))
                if (!Regex.IsMatch(part, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant) ||
                    part.Contains("..") || part.EndsWith(".", StringComparison.Ordinal) ||
                    Regex.IsMatch(part, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw Error("InvalidPath");
            return value;
        }

        internal static ManagedExtensionValidationException Error(string code) { return new ManagedExtensionValidationException(code); }
    }
}
