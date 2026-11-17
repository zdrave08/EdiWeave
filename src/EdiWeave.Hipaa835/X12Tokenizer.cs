// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace EdiWeave.Hipaa835
{
    /// <summary>
    /// Delimiters of an X12 interchange, taken from its ISA segment.
    /// </summary>
    public sealed class X12Delimiters
    {
        /// <summary>Data element separator (ISA position 4).</summary>
        public char Element { get; internal set; }

        /// <summary>Component element separator (ISA16).</summary>
        public char Component { get; internal set; }

        /// <summary>Repetition separator (ISA11 in 5010), or null when ISA11 is 'U'.</summary>
        public char? Repetition { get; internal set; }

        /// <summary>Segment terminator.</summary>
        public char Segment { get; internal set; }
    }

    /// <summary>
    /// One X12 segment split into elements. Element 0 is the segment ID.
    /// </summary>
    public sealed class X12Segment
    {
        private readonly string[] _elements;

        internal X12Segment(string[] elements, int index)
        {
            _elements = elements;
            Index = index;
        }

        /// <summary>Zero-based position of the segment in the interchange.</summary>
        public int Index { get; }

        /// <summary>Segment ID, for example "CLP".</summary>
        public string Id => _elements[0];

        /// <summary>Number of data elements, not counting the segment ID.</summary>
        public int Count => _elements.Length - 1;

        /// <summary>
        /// Returns data element <paramref name="position"/> (1-based, as in implementation guides),
        /// or an empty string when it is absent.
        /// </summary>
        public string this[int position] =>
            position > 0 && position < _elements.Length ? _elements[position] : string.Empty;

        /// <summary>Original text of the segment, without the terminator.</summary>
        public string Raw { get; internal set; }

        /// <inheritdoc />
        public override string ToString() => Raw;
    }

    /// <summary>
    /// Splits X12 text into segments. Detects delimiters from the ISA segment and tolerates
    /// line breaks between segments and an ISA whose fields are not padded to fixed width.
    /// </summary>
    public static class X12Tokenizer
    {
        /// <summary>
        /// Tokenizes an X12 interchange.
        /// </summary>
        /// <exception cref="FormatException">The text does not start with a usable ISA segment.</exception>
        public static IReadOnlyList<X12Segment> Tokenize(string text, out X12Delimiters delimiters)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var start = SkipLeading(text);
            if (text.Length - start < 4 || string.CompareOrdinal(text, start, "ISA", 0, 3) != 0)
                throw new FormatException("The data does not start with an ISA segment.");

            delimiters = DetectDelimiters(text, start);

            var segments = new List<X12Segment>();
            var position = start;
            while (position < text.Length)
            {
                var end = text.IndexOf(delimiters.Segment, position);
                if (end < 0) end = text.Length;

                var raw = TrimLineBreaks(text.Substring(position, end - position));
                if (raw.Length > 0)
                {
                    var elements = raw.Split(delimiters.Element);
                    elements[0] = elements[0].Trim();
                    segments.Add(new X12Segment(elements, segments.Count) { Raw = raw });
                }

                position = end + 1;
            }

            return segments;
        }

        /// <summary>
        /// Splits a composite element (for example "HC:99214:25") into its components.
        /// </summary>
        public static string[] SplitComponents(string value, X12Delimiters delimiters)
        {
            if (string.IsNullOrEmpty(value)) return new string[0];
            return value.Split(delimiters.Component);
        }

        private static X12Delimiters DetectDelimiters(string text, int start)
        {
            var element = text[start + 3];
            if (char.IsLetterOrDigit(element) || element == ' ' || element == '\r' || element == '\n')
                throw new FormatException("ISA element separator is not valid.");

            // Walk to the 16th element separator. ISA16 (component separator) follows it,
            // and the segment terminator follows ISA16. This works for fixed-width and
            // non-padded ISA segments alike.
            var separators = 0;
            var position = start + 3;
            var fields = new List<string>();
            var fieldStart = position + 1;
            while (position < text.Length && separators < 16)
            {
                if (text[position] == element)
                {
                    if (separators > 0) fields.Add(text.Substring(fieldStart, position - fieldStart));
                    separators++;
                    fieldStart = position + 1;
                }
                position++;
            }

            if (separators < 16 || position + 1 >= text.Length)
                throw new FormatException("ISA segment is incomplete: expected 16 data elements.");

            var component = text[position];
            var segment = text[position + 1];
            if (segment == element || segment == component)
                throw new FormatException("ISA segment terminator is not valid.");

            // ISA11 is the repetition separator from version 00402 on; 'U' means "not used".
            char? repetition = null;
            var isa11 = fields.Count >= 11 ? fields[10] : string.Empty;
            if (isa11.Length == 1 && isa11[0] != 'U' && !char.IsLetterOrDigit(isa11[0]))
                repetition = isa11[0];

            return new X12Delimiters
            {
                Element = element,
                Component = component,
                Repetition = repetition,
                Segment = segment
            };
        }

        private static int SkipLeading(string text)
        {
            var i = 0;
            while (i < text.Length && (text[i] == '﻿' || char.IsWhiteSpace(text[i]))) i++;
            return i;
        }

        private static string TrimLineBreaks(string value)
        {
            return value.Trim('\r', '\n');
        }
    }
}
