// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using EdiWeave.Hipaa835;

namespace EdiWeave.Anonymizer
{
    /// <summary>Result of anonymizing one X12 interchange.</summary>
    public sealed class AnonymizationResult
    {
        /// <summary>Anonymized text. Null when <see cref="Leaks"/> is not empty.</summary>
        public string Text { get; internal set; }

        /// <summary>ST01 of the first transaction set, for example 837 or 835.</summary>
        public string TransactionType { get; internal set; }

        /// <summary>Payer organization name (NM1*PR or N1*PR). Not patient data.</summary>
        public string PayerName { get; internal set; }

        /// <summary>Number of element values replaced.</summary>
        public int Replacements { get; internal set; }

        /// <summary>
        /// Segment IDs where an original patient value was still found after anonymization.
        /// The values themselves are never reported. When not empty, <see cref="Text"/> is null.
        /// </summary>
        public List<string> Leaks { get; } = new List<string>();
    }

    /// <summary>
    /// Replaces patient data in X12 837/835 files while keeping the structure, codes and amounts
    /// that tests need. Pseudonyms are deterministic for a given key, so the same patient or claim
    /// number gets the same pseudonym in every file (837 CLM01 still matches 835 CLP01).
    /// </summary>
    public sealed class X12Anonymizer
    {
        // NM101 entity codes for the patient, subscriber and related persons.
        private static readonly HashSet<string> PatientEntities = new HashSet<string> { "IL", "QC", "74", "QD" };

        // REF01 qualifiers that identify a patient, policy or claim.
        private static readonly HashSet<string> SensitiveReferences = new HashSet<string>
        {
            "SY", "1W", "IG", "23", "1L", "F8", "EA", "D9", "6R", "9F", "G1", "F5", "Y4", "1K", "CE", "HJ", "BB", "6P"
        };

        // Segments whose data elements are free text.
        private static readonly HashSet<string> FreeTextSegments = new HashSet<string> { "NTE", "K3", "MSG" };

        private readonly byte[] _key;

        /// <summary>Days added to every date (negative). Same for all files anonymized with the same key.</summary>
        public int DateShiftDays { get; }

        /// <summary>Creates an anonymizer. The key must stay in the clinic; it is never written to output.</summary>
        public X12Anonymizer(byte[] key)
        {
            if (key == null || key.Length < 16)
                throw new ArgumentException("The key must be at least 16 bytes.", nameof(key));
            _key = (byte[])key.Clone();
            var shift = Hash("date-shift", "");
            DateShiftDays = -(30 + (shift[0] << 8 | shift[1]) % 300);
        }

        /// <summary>Anonymizes one X12 interchange.</summary>
        public AnonymizationResult Anonymize(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            X12Tokenizer.Tokenize(text, out var delimiters);
            var result = new AnonymizationResult();
            var sensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var output = new StringBuilder(text.Length);
            var patientContext = false;

            var position = 0;
            while (position < text.Length)
            {
                var end = text.IndexOf(delimiters.Segment, position);
                var chunk = end < 0 ? text.Substring(position) : text.Substring(position, end - position);
                var lead = chunk.Length - chunk.TrimStart('\r', '\n').Length;
                var core = chunk.Substring(lead).TrimEnd('\r', '\n');
                var trail = chunk.Substring(lead + core.Length);

                output.Append(chunk, 0, lead);
                if (core.Length > 0 && !core.StartsWith("ISA", StringComparison.Ordinal))
                {
                    var e = core.Split(delimiters.Element);
                    patientContext = Process(e, patientContext, delimiters, result, sensitive);
                    core = string.Join(delimiters.Element.ToString(), e);
                }

                output.Append(core).Append(trail);
                if (end < 0) break;
                output.Append(delimiters.Segment);
                position = end + 1;
            }

            var anonymized = output.ToString();
            FindLeaks(anonymized, delimiters, sensitive, result);
            if (result.Leaks.Count == 0)
                result.Text = anonymized;
            return result;
        }

        private bool Process(string[] e, bool patientContext, X12Delimiters d, AnonymizationResult r,
            HashSet<string> sensitive)
        {
            string Get(int i) => i < e.Length ? e[i] : string.Empty;

            // Values shorter than 3 characters (initials, "M"/"F") also occur as codes elsewhere
            // and are not identifying on their own, so the leak check ignores them.
            void Record(string value)
            {
                if (value.Trim().Length >= 3) sensitive.Add(value.Trim());
            }

            void Replace(int i, string category, bool record = true)
            {
                if (i >= e.Length || string.IsNullOrEmpty(e[i])) return;
                if (record) Record(e[i]);
                e[i] = Pseudonym(category, e[i]);
                r.Replacements++;
            }

            void Set(int i, string value, bool record = true)
            {
                if (i >= e.Length || string.IsNullOrEmpty(e[i])) return;
                if (record) Record(e[i]);
                e[i] = value;
                r.Replacements++;
            }

            switch (e[0])
            {
                case "ST":
                    if (r.TransactionType == null) r.TransactionType = Get(1);
                    return false;
                case "HL":
                case "LX":
                case "CLM":
                case "CLP":
                case "SVC":
                case "SV1":
                case "SV2":
                case "N1":
                    if (e[0] == "CLM" || e[0] == "CLP") Replace(1, "patient-control-number");
                    if (e[0] == "CLP") Replace(7, "payer-claim-number");
                    if (e[0] == "N1" && Get(1) == "PR" && r.PayerName == null) r.PayerName = Get(2);
                    return false;
                case "NM1":
                    if (Get(1) == "PR" && r.PayerName == null) r.PayerName = Get(3);
                    if (!PatientEntities.Contains(Get(1))) return false;
                    Replace(3, "name");
                    Replace(4, "name");
                    Replace(5, "name");
                    Replace(7, "name");
                    Replace(9, "member-id");
                    return true;
                case "N3":
                    if (!patientContext) return false;
                    Set(1, "1 SYNTHETIC STREET");
                    Set(2, "UNIT 1");
                    return true;
                case "N4":
                    // City, state and country are kept: they are shared by many people and are needed
                    // to test address rules. The postal code is replaced.
                    if (patientContext) Set(3, "00000", record: false);
                    return patientContext;
                case "DMG":
                    if (Get(1) == "D8" && Get(2).Length == 8)
                        Set(2, Get(2).Substring(0, 4) + "0101");
                    return patientContext;
                case "PER":
                    if (patientContext)
                    {
                        for (var i = 2; i < e.Length; i++)
                            if (!string.IsNullOrEmpty(e[i]) && !IsQualifier(Get(i - 1), e[i]))
                                Replace(i, "contact");
                    }
                    return patientContext;
                case "SBR":
                    Replace(3, "group-number");
                    Set(4, string.IsNullOrEmpty(Get(4)) ? Get(4) : "SYNTHETIC GROUP");
                    return false;
                case "REF":
                    if (SensitiveReferences.Contains(Get(1))) Replace(2, "ref-" + Get(1));
                    return patientContext;
                case "DTP":
                    ShiftDates(e, 2, 3, r);
                    return patientContext;
                case "DTM":
                    ShiftDate(e, 2, r);
                    return patientContext;
                case "PAT":
                    if (Get(5) == "D8") ShiftDate(e, 6, r);
                    return patientContext;
                default:
                    if (FreeTextSegments.Contains(e[0]))
                    {
                        for (var i = 2; i < e.Length; i++) Set(i, string.IsNullOrEmpty(e[i]) ? e[i] : "REDACTED", record: false);
                        if (e[0] == "K3" || e[0] == "MSG") Set(1, "REDACTED", record: false);
                    }
                    return patientContext;
            }
        }

        private static bool IsQualifier(string previous, string value)
        {
            // PER: function code, name, then (qualifier, number) pairs. Keep the communication qualifiers.
            return value.Length == 2 && value.All(char.IsUpper) && (value == "TE" || value == "EM" || value == "FX" || value == "EX");
        }

        private void ShiftDates(string[] e, int formatIndex, int valueIndex, AnonymizationResult r)
        {
            if (valueIndex >= e.Length) return;
            var format = e[formatIndex];
            if (format == "D8") ShiftDate(e, valueIndex, r);
            else if (format == "RD8")
            {
                var parts = e[valueIndex].Split('-');
                if (parts.Length != 2) return;
                var from = Shift(parts[0]);
                var to = Shift(parts[1]);
                if (from == null || to == null) return;
                e[valueIndex] = from + "-" + to;
                r.Replacements++;
            }
        }

        private void ShiftDate(string[] e, int index, AnonymizationResult r)
        {
            if (index >= e.Length) return;
            var shifted = Shift(e[index]);
            if (shifted == null) return;
            e[index] = shifted;
            r.Replacements++;
        }

        private string Shift(string yyyymmdd)
        {
            if (!DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return null;
            return date.AddDays(DateShiftDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Format-preserving pseudonym: digits stay digits, letters stay letters (same case),
        /// everything else (dashes, spaces) is kept. Deterministic for the key and category.
        /// </summary>
        public string Pseudonym(string category, string value)
        {
            var hash = Hash(category, value.ToUpperInvariant());
            var chars = value.ToCharArray();
            var block = 0;
            for (var i = 0; i < chars.Length; i++)
            {
                if (i > 0 && i % hash.Length == 0)
                    hash = Hash(category + "#" + (++block), value.ToUpperInvariant());
                var b = hash[i % hash.Length];
                var c = chars[i];
                if (c >= '0' && c <= '9') chars[i] = (char)('0' + b % 10);
                else if (c >= 'A' && c <= 'Z') chars[i] = (char)('A' + b % 26);
                else if (c >= 'a' && c <= 'z') chars[i] = (char)('a' + b % 26);
            }

            var pseudonym = new string(chars);
            // Never return the original (possible only for very short values).
            return string.Equals(pseudonym, value, StringComparison.OrdinalIgnoreCase) && value.Any(char.IsLetterOrDigit)
                ? Pseudonym(category + "!", value)
                : pseudonym;
        }

        private byte[] Hash(string category, string value)
        {
            using (var hmac = new HMACSHA256(_key))
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(category + "\u0001" + value));
        }

        private static void FindLeaks(string text, X12Delimiters d, HashSet<string> sensitive, AnonymizationResult r)
        {
            if (sensitive.Count == 0) return;

            foreach (var raw in text.Split(d.Segment))
            {
                var segment = raw.Trim('\r', '\n');
                if (segment.Length == 0) continue;
                var elements = segment.Split(d.Element);
                var hit = false;
                foreach (var element in elements.Skip(1))
                {
                    foreach (var value in element.Split(d.Component))
                    {
                        if (value.Length > 0 && sensitive.Contains(value)) hit = true;
                    }
                }

                // Longer values must not appear anywhere, for example inside free text.
                if (!hit)
                    hit = sensitive.Any(s => s.Length >= 5 && segment.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

                if (hit) r.Leaks.Add(elements[0]);
            }
        }
    }
}
