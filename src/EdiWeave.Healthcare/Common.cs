// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using EdiWeave.Hipaa835;

namespace EdiWeave.Healthcare
{
    /// <summary>A problem found while validating data before it is sent. Messages never contain patient values.</summary>
    public sealed class ValidationIssue
    {
        /// <summary>Creates an issue.</summary>
        public ValidationIssue(IssueSeverity severity, string code, string path, string message)
        {
            Severity = severity;
            Code = code;
            Path = path;
            Message = message;
        }

        /// <summary>Warning or error. Errors stop <see cref="Claim837PWriter.Write"/>.</summary>
        public IssueSeverity Severity { get; }

        /// <summary>Short stable code, for example "NPI_INVALID".</summary>
        public string Code { get; }

        /// <summary>Where the problem is, for example "Claims[2].ServiceLines[0].ProcedureCode".</summary>
        public string Path { get; }

        /// <summary>Description without any patient data.</summary>
        public string Message { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Severity} {Code} at {Path}: {Message}";
    }

    /// <summary>Settings for the ISA/GS envelope and delimiters of files this library writes.</summary>
    public sealed class X12Options
    {
        /// <summary>ISA05 sender qualifier.</summary>
        public string SenderQualifier { get; set; } = "ZZ";

        /// <summary>ISA06 sender ID (up to 15 characters).</summary>
        public string SenderId { get; set; }

        /// <summary>ISA07 receiver qualifier.</summary>
        public string ReceiverQualifier { get; set; } = "ZZ";

        /// <summary>ISA08 receiver ID (up to 15 characters).</summary>
        public string ReceiverId { get; set; }

        /// <summary>GS02. Defaults to <see cref="SenderId"/>.</summary>
        public string ApplicationSenderCode { get; set; }

        /// <summary>GS03. Defaults to <see cref="ReceiverId"/>.</summary>
        public string ApplicationReceiverCode { get; set; }

        /// <summary>True for production (ISA15 = P), false for test (T).</summary>
        public bool Production { get; set; }

        /// <summary>ISA13 interchange control number (1..999999999).</summary>
        public int InterchangeControlNumber { get; set; } = 1;

        /// <summary>GS06 group control number.</summary>
        public int GroupControlNumber { get; set; } = 1;

        /// <summary>Date and time written in the envelope. Defaults to the current local time.</summary>
        public DateTime? Timestamp { get; set; }

        /// <summary>Data element separator.</summary>
        public char ElementSeparator { get; set; } = '*';

        /// <summary>Component element separator (ISA16).</summary>
        public char ComponentSeparator { get; set; } = ':';

        /// <summary>Repetition separator (ISA11).</summary>
        public char RepetitionSeparator { get; set; } = '^';

        /// <summary>Segment terminator.</summary>
        public char SegmentTerminator { get; set; } = '~';

        /// <summary>Adds a line break after each segment (easier to read, still accepted by most receivers).</summary>
        public bool NewLineAfterSegment { get; set; }

        internal void Check()
        {
            if (string.IsNullOrWhiteSpace(SenderId)) throw new InvalidOperationException("X12Options.SenderId is required.");
            if (string.IsNullOrWhiteSpace(ReceiverId)) throw new InvalidOperationException("X12Options.ReceiverId is required.");
            if (SenderId.Length > 15 || ReceiverId.Length > 15)
                throw new InvalidOperationException("ISA sender and receiver IDs are at most 15 characters.");
            if (InterchangeControlNumber < 1 || InterchangeControlNumber > 999999999)
                throw new InvalidOperationException("InterchangeControlNumber must be 1..999999999.");
            var distinct = new[] { ElementSeparator, ComponentSeparator, RepetitionSeparator, SegmentTerminator };
            if (distinct.Distinct().Count() != 4)
                throw new InvalidOperationException("Delimiters must be four different characters.");
        }
    }

    /// <summary>Builds one X12 interchange with a single functional group and a single transaction set.</summary>
    internal sealed class X12Document
    {
        private readonly X12Options _o;
        private readonly string _functionalId;
        private readonly string _transaction;
        private readonly string _reference;
        private readonly List<string> _body = new List<string>();
        private readonly DateTime _now;

        public X12Document(X12Options options, string functionalId, string transaction, string reference)
        {
            options.Check();
            _o = options;
            _functionalId = functionalId;
            _transaction = transaction;
            _reference = reference;
            _now = options.Timestamp ?? DateTime.Now;
        }

        public int BodyCount => _body.Count;

        /// <summary>Adds a segment. Trailing empty elements are dropped; delimiter characters in values are removed.</summary>
        public void Seg(params string[] elements)
        {
            var list = elements.Select(Clean).ToList();
            while (list.Count > 1 && list[list.Count - 1].Length == 0) list.RemoveAt(list.Count - 1);
            _body.Add(string.Join(_o.ElementSeparator.ToString(), list)
                .Replace(CompositeMark, _o.ComponentSeparator));
        }

        /// <summary>Joins composite parts with the component separator, dropping trailing empty parts.</summary>
        public string Comp(params string[] parts)
        {
            var list = parts.Select(Clean).ToList();
            while (list.Count > 1 && list[list.Count - 1].Length == 0) list.RemoveAt(list.Count - 1);
            // The mark is replaced with the real component separator in Seg, after delimiters in data are removed.
            return string.Join(CompositeMark.ToString(), list);
        }

        private const char CompositeMark = '';

        private string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c == _o.ElementSeparator || c == _o.ComponentSeparator || c == _o.RepetitionSeparator ||
                    c == _o.SegmentTerminator || c == '\r' || c == '\n')
                    sb.Append(' ');
                else
                    sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public string Build()
        {
            var e = _o.ElementSeparator.ToString();
            var all = new List<string>();
            var date6 = _now.ToString("yyMMdd", CultureInfo.InvariantCulture);
            var date8 = _now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var time = _now.ToString("HHmm", CultureInfo.InvariantCulture);
            var icn = _o.InterchangeControlNumber.ToString("D9", CultureInfo.InvariantCulture);

            all.Add(string.Join(e, "ISA", "00", new string(' ', 10), "00", new string(' ', 10),
                Pad(_o.SenderQualifier, 2), Pad(_o.SenderId, 15), Pad(_o.ReceiverQualifier, 2), Pad(_o.ReceiverId, 15),
                date6, time, _o.RepetitionSeparator.ToString(), "00501", icn, "0", _o.Production ? "P" : "T",
                _o.ComponentSeparator.ToString()));
            all.Add(string.Join(e, "GS", _functionalId, Clean(_o.ApplicationSenderCode ?? _o.SenderId),
                Clean(_o.ApplicationReceiverCode ?? _o.ReceiverId), date8, time,
                _o.GroupControlNumber.ToString(CultureInfo.InvariantCulture), "X", _reference));
            all.Add(string.Join(e, "ST", _transaction, "0001", _reference));
            all.AddRange(_body);
            all.Add(string.Join(e, "SE", (_body.Count + 2).ToString(CultureInfo.InvariantCulture), "0001"));
            all.Add(string.Join(e, "GE", "1", _o.GroupControlNumber.ToString(CultureInfo.InvariantCulture)));
            all.Add(string.Join(e, "IEA", "1", icn));

            var terminator = _o.SegmentTerminator.ToString() + (_o.NewLineAfterSegment ? "\r\n" : string.Empty);
            return string.Concat(all.Select(s => s + terminator));
        }

        private static string Pad(string value, int width)
        {
            value = value ?? string.Empty;
            return value.Length >= width ? value.Substring(0, width) : value.PadRight(width);
        }
    }

    internal static class Fmt
    {
        public static string Amount(decimal value) =>
            decimal.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

        public static string Date(DateTime value) => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        public static string Digits(string value) =>
            new string((value ?? string.Empty).Where(char.IsDigit).ToArray());

        public static decimal? ParseDecimal(string value)
        {
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
        }

        public static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.Length >= 8 && DateTime.TryParseExact(value.Substring(0, 8), "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
            return null;
        }
    }

    /// <summary>Checks for identifiers used in claims.</summary>
    public static class Identifiers
    {
        /// <summary>
        /// Validates a National Provider Identifier: 10 digits starting with 1 or 2, with the Luhn check digit
        /// calculated over the number prefixed by 80840.
        /// </summary>
        public static bool IsValidNpi(string npi)
        {
            if (npi == null || npi.Length != 10 || !npi.All(c => c >= '0' && c <= '9')) return false;
            if (npi[0] != '1' && npi[0] != '2') return false;
            var digits = "80840" + npi;
            var sum = 0;
            var alternate = false;
            for (var i = digits.Length - 1; i >= 0; i--)
            {
                var n = digits[i] - '0';
                if (alternate)
                {
                    n *= 2;
                    if (n > 9) n -= 9;
                }
                sum += n;
                alternate = !alternate;
            }
            return sum % 10 == 0;
        }

        /// <summary>ICD-10-CM shape: a letter (not U), a digit, a digit or A/B, then up to four alphanumerics, no dot.</summary>
        public static bool IsValidIcd10Shape(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length < 3 || code.Length > 7) return false;
            if (!(code[0] >= 'A' && code[0] <= 'Z') || code[0] == 'U') return false;
            if (!char.IsDigit(code[1])) return false;
            if (!(char.IsDigit(code[2]) || code[2] == 'A' || code[2] == 'B')) return false;
            for (var i = 3; i < code.Length; i++)
                if (!((code[i] >= '0' && code[i] <= '9') || (code[i] >= 'A' && code[i] <= 'Z'))) return false;
            return true;
        }

        /// <summary>CPT (5 digits, or 4 digits and F/T/U) or HCPCS Level II (letter A-V and 4 digits) shape.</summary>
        public static bool IsValidProcedureCodeShape(string code)
        {
            if (code == null || code.Length != 5) return false;
            if (code.All(char.IsDigit)) return true;
            if (code.Substring(0, 4).All(char.IsDigit) && "FTU".IndexOf(code[4]) >= 0) return true;
            return code[0] >= 'A' && code[0] <= 'V' && code.Substring(1).All(char.IsDigit);
        }

        /// <summary>Employer identification number: 9 digits, optionally written 12-3456789.</summary>
        public static bool IsValidTin(string tin) =>
            !string.IsNullOrEmpty(tin) && Fmt.Digits(tin).Length == 9 && tin.All(c => char.IsDigit(c) || c == '-');
    }
}
