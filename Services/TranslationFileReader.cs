using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace KillerScan.Services
{
    /// <summary>
    /// Reads an external translation file (--lang-file) as data only. XamlReader would build any
    /// object the markup names, so a translation is parsed as plain XML and accepted only when it is
    /// the locale file shape: a ResourceDictionary of keyed sys:String and sys:Double entries whose
    /// keys already exist in the built-in English strings.
    /// </summary>
    internal static class TranslationFileReader
    {
        internal const long MaximumFileBytes = 8L * 1024 * 1024;
        private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";

        /// <summary>Returns the entries, or throws <see cref="FormatException"/> naming the first problem.</summary>
        internal static Dictionary<string, object> Read(Stream stream, ICollection<string> knownKeys)
        {
            if (stream.CanSeek && stream.Length > MaximumFileBytes)
                throw new FormatException("The translation file is too large.");
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaximumFileBytes
            };
            var entries = new Dictionary<string, object>(StringComparer.Ordinal);
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();
            if (reader.LocalName != "ResourceDictionary" || reader.NamespaceURI != PresentationNamespace)
                throw new FormatException("The root element must be a ResourceDictionary.");
            for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                if (reader.Prefix != "xmlns" && reader.Name != "xmlns")
                    throw new FormatException($"The ResourceDictionary attribute {reader.Name} is not allowed.");
            reader.MoveToElement();
            if (reader.IsEmptyElement) return entries;
            reader.Read();
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                    throw new FormatException("Only keyed string and number entries are allowed.");
                bool isString = reader.LocalName == "String", isDouble = reader.LocalName == "Double";
                if (!(isString || isDouble) || !IsSystemNamespace(reader.NamespaceURI))
                    throw new FormatException($"The element {reader.Name} is not allowed.");
                string? key = null;
                for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == "Key" && reader.NamespaceURI == XamlNamespace) key = reader.Value;
                    else if (!(reader.LocalName == "space" && reader.NamespaceURI == XmlNamespace))
                        throw new FormatException($"The attribute {reader.Name} is not allowed.");
                }
                reader.MoveToElement();
                if (key is null || key.Length == 0) throw new FormatException("Every entry needs an x:Key.");
                if (!knownKeys.Contains(key)) throw new FormatException($"The key {key} is not in the English strings.");
                if (entries.ContainsKey(key)) throw new FormatException($"The key {key} appears twice.");
                string text;
                if (reader.IsEmptyElement) { text = string.Empty; reader.Read(); }
                else text = ReadText(reader, key);
                entries[key] = isString ? text
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                        && !double.IsNaN(number) && !double.IsInfinity(number) ? number
                    : throw new FormatException($"The entry {key} is not a number.");
            }
            return entries;
        }

        private static string ReadText(XmlReader reader, string key)
        {
            reader.Read();
            string text = string.Empty;
            while (reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace
                    or XmlNodeType.Whitespace))
                    throw new FormatException($"The entry {key} may contain only text.");
                text += reader.Value;
                reader.Read();
            }
            reader.Read();
            return text;
        }

        private static bool IsSystemNamespace(string uri) =>
            uri.StartsWith("clr-namespace:System;assembly=", StringComparison.Ordinal)
            && uri.Substring("clr-namespace:System;assembly=".Length) is "mscorlib" or "System.Runtime" or "netstandard";
    }
}
