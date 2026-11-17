// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using EdiWeave.Hipaa835;

namespace EdiWeave.Healthcare
{
    /// <summary>The kind of transaction in an X12 file, from its ST01.</summary>
    public enum EdiKind
    {
        /// <summary>Not recognized or not a readable X12 file.</summary>
        Unknown,

        /// <summary>837 claim.</summary>
        Claim837,

        /// <summary>835 payment advice.</summary>
        Remittance835,

        /// <summary>999 implementation acknowledgement.</summary>
        Ack999,

        /// <summary>TA1 interchange acknowledgement (no ST segment).</summary>
        InterchangeAckTa1,

        /// <summary>277 claim acknowledgement (277CA) or status response.</summary>
        ClaimStatus277,

        /// <summary>270 eligibility request.</summary>
        Eligibility270,

        /// <summary>271 eligibility response.</summary>
        Eligibility271
    }

    /// <summary>Identifies what an incoming X12 file is, so it can be routed to the right reader.</summary>
    public static class EdiClassifier
    {
        /// <summary>Returns the kind of the first transaction in the file, or <see cref="EdiKind.Unknown"/>.</summary>
        public static EdiKind Classify(string text)
        {
            if (string.IsNullOrEmpty(text)) return EdiKind.Unknown;
            IReadOnlyList<X12Segment> segments;
            try { segments = X12Tokenizer.Tokenize(text, out _); }
            catch (FormatException) { return EdiKind.Unknown; }

            var st = segments.FirstOrDefault(s => s.Id == "ST");
            if (st == null) return segments.Any(s => s.Id == "TA1") ? EdiKind.InterchangeAckTa1 : EdiKind.Unknown;
            switch (st[1])
            {
                case "837": return EdiKind.Claim837;
                case "835": return EdiKind.Remittance835;
                case "999": return EdiKind.Ack999;
                case "277": return EdiKind.ClaimStatus277;
                case "270": return EdiKind.Eligibility270;
                case "271": return EdiKind.Eligibility271;
                default: return EdiKind.Unknown;
            }
        }
    }

    /// <summary>TA1: the interchange was received (or not) at envelope level.</summary>
    public sealed class InterchangeAck
    {
        /// <summary>ISA13 of the interchange being acknowledged.</summary>
        public string InterchangeControlNumber { get; internal set; }

        /// <summary>TA104: A accepted, E accepted with errors, R rejected.</summary>
        public string AckCode { get; internal set; }

        /// <summary>TA105 note code explaining a rejection (for example 023 = invalid interchange ID).</summary>
        public string NoteCode { get; internal set; }

        /// <summary>True when AckCode is A or E.</summary>
        public bool IsAccepted => AckCode == "A" || AckCode == "E";
    }

    /// <summary>A syntax error in a transaction set reported by a 999 (IK3/IK4).</summary>
    public sealed class SyntaxError
    {
        /// <summary>IK301 segment that has the error.</summary>
        public string SegmentId { get; internal set; }

        /// <summary>IK302 position of the segment in the transaction set.</summary>
        public int? SegmentPosition { get; internal set; }

        /// <summary>IK303 loop identifier, for example 2300.</summary>
        public string LoopId { get; internal set; }

        /// <summary>IK304 segment syntax error code.</summary>
        public string SegmentErrorCode { get; internal set; }

        /// <summary>IK402 data element reference number, when the error is on an element.</summary>
        public string ElementReference { get; internal set; }

        /// <summary>IK401 position of the element in the segment, when the error is on an element.</summary>
        public int? ElementPosition { get; internal set; }

        /// <summary>IK403 data element syntax error code.</summary>
        public string ElementErrorCode { get; internal set; }
    }

    /// <summary>The result for one transaction set in a 999 (AK2/IK5).</summary>
    public sealed class TransactionSetAck
    {
        /// <summary>AK201, for example 837.</summary>
        public string TransactionSetId { get; internal set; }

        /// <summary>AK202 control number of the set (ST02).</summary>
        public string ControlNumber { get; internal set; }

        /// <summary>IK501: A accepted, E accepted with errors, R rejected, M, W, X.</summary>
        public string Status { get; internal set; }

        /// <summary>IK502 to IK506 transaction set syntax error codes.</summary>
        public List<string> ErrorCodes { get; } = new List<string>();

        /// <summary>Segment and element errors. Bad values are deliberately not kept (they are patient data).</summary>
        public List<SyntaxError> Errors { get; } = new List<SyntaxError>();

        /// <summary>True when Status is A or E.</summary>
        public bool IsAccepted => Status == "A" || Status == "E";
    }

    /// <summary>A 999 implementation acknowledgement for one functional group.</summary>
    public sealed class ImplementationAck
    {
        /// <summary>AK101 functional identifier of the group acknowledged (HC for claims).</summary>
        public string FunctionalId { get; internal set; }

        /// <summary>AK102 group control number (GS06 of the file you sent).</summary>
        public string GroupControlNumber { get; internal set; }

        /// <summary>AK103 version, for example 005010X222A1.</summary>
        public string Version { get; internal set; }

        /// <summary>AK901: A accepted, E accepted with errors, R rejected, P partially accepted.</summary>
        public string GroupStatus { get; internal set; }

        /// <summary>AK902 number of transaction sets included.</summary>
        public int? SetsIncluded { get; internal set; }

        /// <summary>AK903 number received.</summary>
        public int? SetsReceived { get; internal set; }

        /// <summary>AK904 number accepted.</summary>
        public int? SetsAccepted { get; internal set; }

        /// <summary>AK905 to AK909 functional group syntax error codes.</summary>
        public List<string> GroupErrorCodes { get; } = new List<string>();

        /// <summary>Per transaction set results.</summary>
        public List<TransactionSetAck> TransactionSets { get; } = new List<TransactionSetAck>();

        /// <summary>True when the whole group was accepted (A or E).</summary>
        public bool IsAccepted => GroupStatus == "A" || GroupStatus == "E";
    }

    /// <summary>Result of reading acknowledgement files.</summary>
    public sealed class AckResult
    {
        /// <summary>ISA13 of the file read.</summary>
        public string InterchangeControlNumber { get; internal set; }

        /// <summary>TA1 acknowledgements found.</summary>
        public List<InterchangeAck> InterchangeAcks { get; } = new List<InterchangeAck>();

        /// <summary>999 acknowledgements found.</summary>
        public List<ImplementationAck> ImplementationAcks { get; } = new List<ImplementationAck>();

        /// <summary>Problems reading the file.</summary>
        public List<Remittance835Issue> Issues { get; } = new List<Remittance835Issue>();
    }

    /// <summary>Reads TA1 and 999 acknowledgements.</summary>
    public static class AckReader
    {
        /// <summary>Reads TA1 and 999 acknowledgements from X12 text. Never throws on bad content.</summary>
        public static AckResult Read(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var result = new AckResult();
            IReadOnlyList<X12Segment> segments;
            try { segments = X12Tokenizer.Tokenize(text, out _); }
            catch (FormatException ex)
            {
                result.Issues.Add(new Remittance835Issue(IssueSeverity.Error, ex.Message));
                return result;
            }

            result.InterchangeControlNumber = segments.FirstOrDefault(s => s.Id == "ISA")?[13];

            ImplementationAck group = null;
            TransactionSetAck set = null;
            SyntaxError lastSegmentError = null;

            foreach (var s in segments)
            {
                switch (s.Id)
                {
                    case "TA1":
                        result.InterchangeAcks.Add(new InterchangeAck
                        {
                            InterchangeControlNumber = s[1],
                            AckCode = s[4],
                            NoteCode = s[5]
                        });
                        break;
                    case "AK1":
                        group = new ImplementationAck
                        {
                            FunctionalId = s[1],
                            GroupControlNumber = s[2],
                            Version = s[3]
                        };
                        result.ImplementationAcks.Add(group);
                        break;
                    case "AK2":
                        set = new TransactionSetAck { TransactionSetId = s[1], ControlNumber = s[2] };
                        group?.TransactionSets.Add(set);
                        lastSegmentError = null;
                        break;
                    case "IK3":
                        lastSegmentError = new SyntaxError
                        {
                            SegmentId = s[1],
                            SegmentPosition = Int(s[2]),
                            LoopId = s[3],
                            SegmentErrorCode = s[4]
                        };
                        set?.Errors.Add(lastSegmentError);
                        break;
                    case "IK4":
                        // An element error belongs to the preceding IK3. Keep one entry per element error.
                        var element = new SyntaxError
                        {
                            SegmentId = lastSegmentError?.SegmentId,
                            SegmentPosition = lastSegmentError?.SegmentPosition,
                            LoopId = lastSegmentError?.LoopId,
                            SegmentErrorCode = lastSegmentError?.SegmentErrorCode,
                            ElementPosition = Int(s[1]),
                            ElementReference = s[2],
                            ElementErrorCode = s[3]
                        };
                        set?.Errors.Add(element);
                        break;
                    case "IK5":
                        if (set != null)
                        {
                            set.Status = s[1];
                            for (var i = 2; i <= 6; i++)
                                if (!string.IsNullOrEmpty(s[i])) set.ErrorCodes.Add(s[i]);
                        }
                        break;
                    case "AK9":
                        if (group != null)
                        {
                            group.GroupStatus = s[1];
                            group.SetsIncluded = Int(s[2]);
                            group.SetsReceived = Int(s[3]);
                            group.SetsAccepted = Int(s[4]);
                            for (var i = 5; i <= 9; i++)
                                if (!string.IsNullOrEmpty(s[i])) group.GroupErrorCodes.Add(s[i]);
                        }
                        break;
                }
            }

            // An IK3 that was followed by IK4 entries is already represented by those entries.
            foreach (var g in result.ImplementationAcks)
            foreach (var t in g.TransactionSets)
            {
                var withElements = new HashSet<int?>(t.Errors.Where(e => e.ElementPosition.HasValue).Select(e => e.SegmentPosition));
                t.Errors.RemoveAll(e => !e.ElementPosition.HasValue && withElements.Contains(e.SegmentPosition));
            }

            return result;
        }

        private static int? Int(string value) =>
            int.TryParse(value, out var n) ? n : (int?)null;
    }

    /// <summary>How a payer or clearinghouse classified a claim in a 277CA.</summary>
    public enum ClaimStatusOutcome
    {
        /// <summary>Accepted for adjudication (category A1 or A2).</summary>
        Accepted,

        /// <summary>Rejected: unprocessable, invalid or not found (A3, A4, A6, A7, A8). Fix and resubmit.</summary>
        Rejected,

        /// <summary>Pending (category P).</summary>
        Pending,

        /// <summary>Finalized, paid or denied (category F).</summary>
        Finalized,

        /// <summary>The payer asks for more information (category R).</summary>
        RequestForInformation,

        /// <summary>Any other category.</summary>
        Other
    }

    /// <summary>One status entry (STC).</summary>
    public sealed class StatusInfo
    {
        /// <summary>STC01-1 claim status category code, for example A1, A3, F1.</summary>
        public string Category { get; internal set; }

        /// <summary>STC01-2 claim status code, for example 20 (accepted for processing), 187 (date of birth).</summary>
        public string Code { get; internal set; }

        /// <summary>STC01-3 entity identifier, for example 41 (submitter), PR (payer), 85 (billing provider).</summary>
        public string EntityCode { get; internal set; }

        /// <summary>STC02 status effective date.</summary>
        public DateTime? Date { get; internal set; }

        /// <summary>STC04 claim charge amount.</summary>
        public decimal? Amount { get; internal set; }

        /// <summary>STC12 free-form message. Treat it as sensitive: payers sometimes echo patient data.</summary>
        public string Message { get; internal set; }

        /// <summary>The outcome implied by <see cref="Category"/>.</summary>
        public ClaimStatusOutcome Outcome => ClassOf(Category);

        internal static ClaimStatusOutcome ClassOf(string category)
        {
            if (string.IsNullOrEmpty(category)) return ClaimStatusOutcome.Other;
            switch (category)
            {
                case "A1":
                case "A2":
                    return ClaimStatusOutcome.Accepted;
                case "A3":
                case "A4":
                case "A6":
                case "A7":
                case "A8":
                    return ClaimStatusOutcome.Rejected;
            }
            switch (category[0])
            {
                case 'P': return ClaimStatusOutcome.Pending;
                case 'F': return ClaimStatusOutcome.Finalized;
                case 'R': return ClaimStatusOutcome.RequestForInformation;
                default: return ClaimStatusOutcome.Other;
            }
        }
    }

    /// <summary>Status of one service line in a 277CA.</summary>
    public sealed class LineStatus
    {
        /// <summary>Procedure code (SVC01-2).</summary>
        public string ProcedureCode { get; internal set; }

        /// <summary>Line charge (SVC02).</summary>
        public decimal? Charge { get; internal set; }

        /// <summary>Your line control number (REF*FJ), when echoed.</summary>
        public string LineControlNumber { get; internal set; }

        /// <summary>Status entries for the line.</summary>
        public List<StatusInfo> Statuses { get; } = new List<StatusInfo>();
    }

    /// <summary>Status of one claim in a 277CA, matched to your claim by <see cref="PatientControlNumber"/>.</summary>
    public sealed class ClaimStatus
    {
        /// <summary>TRN02: the patient control number you sent in CLM01.</summary>
        public string PatientControlNumber { get; internal set; }

        /// <summary>REF*1K payer claim control number, when assigned.</summary>
        public string PayerClaimControlNumber { get; internal set; }

        /// <summary>REF*D9 clearinghouse trace number.</summary>
        public string ClearinghouseTraceNumber { get; internal set; }

        /// <summary>Claim level status entries.</summary>
        public List<StatusInfo> Statuses { get; } = new List<StatusInfo>();

        /// <summary>Service line statuses.</summary>
        public List<LineStatus> Lines { get; } = new List<LineStatus>();

        /// <summary>Overall outcome: Rejected when any status rejects, otherwise the first status.</summary>
        public ClaimStatusOutcome Outcome
        {
            get
            {
                if (Statuses.Count == 0) return ClaimStatusOutcome.Other;
                return Statuses.Any(s => s.Outcome == ClaimStatusOutcome.Rejected)
                    ? ClaimStatusOutcome.Rejected
                    : Statuses[0].Outcome;
            }
        }
    }

    /// <summary>Result of reading a 277 claim acknowledgement.</summary>
    public sealed class ClaimStatusResult
    {
        /// <summary>Claim statuses, one per TRN.</summary>
        public List<ClaimStatus> Claims { get; } = new List<ClaimStatus>();

        /// <summary>Problems reading the file.</summary>
        public List<Remittance835Issue> Issues { get; } = new List<Remittance835Issue>();
    }

    /// <summary>Reads 277 claim acknowledgements (005010X214) and claim status responses (005010X212).</summary>
    public static class ClaimStatusReader
    {
        /// <summary>Reads a 277 from X12 text. Never throws on bad content.</summary>
        public static ClaimStatusResult Read(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var result = new ClaimStatusResult();
            IReadOnlyList<X12Segment> segments;
            X12Delimiters delimiters;
            try { segments = X12Tokenizer.Tokenize(text, out delimiters); }
            catch (FormatException ex)
            {
                result.Issues.Add(new Remittance835Issue(IssueSeverity.Error, ex.Message));
                return result;
            }

            ClaimStatus claim = null;
            LineStatus line = null;
            var inTransaction = false;

            foreach (var s in segments)
            {
                switch (s.Id)
                {
                    case "ST":
                        inTransaction = s[1] == "277";
                        break;
                    case "HL":
                        // A new hierarchical level closes the current claim.
                        claim = null;
                        line = null;
                        break;
                    case "TRN" when inTransaction && s[1] == "2":
                        claim = new ClaimStatus { PatientControlNumber = s[2] };
                        result.Claims.Add(claim);
                        line = null;
                        break;
                    case "STC" when claim != null:
                        var parts = X12Tokenizer.SplitComponents(s[1], delimiters);
                        var info = new StatusInfo
                        {
                            Category = parts.Length > 0 ? parts[0] : null,
                            Code = parts.Length > 1 ? parts[1] : null,
                            EntityCode = parts.Length > 2 ? parts[2] : null,
                            Date = Fmt.ParseDate(s[2]),
                            Amount = Fmt.ParseDecimal(s[4]),
                            Message = string.IsNullOrEmpty(s[12]) ? null : s[12]
                        };
                        if (line != null) line.Statuses.Add(info); else claim.Statuses.Add(info);
                        break;
                    case "REF" when claim != null:
                        if (s[1] == "1K") claim.PayerClaimControlNumber = s[2];
                        else if (s[1] == "D9") claim.ClearinghouseTraceNumber = s[2];
                        else if (s[1] == "FJ" && line != null) line.LineControlNumber = s[2];
                        break;
                    case "SVC" when claim != null:
                        var proc = X12Tokenizer.SplitComponents(s[1], delimiters);
                        line = new LineStatus
                        {
                            ProcedureCode = proc.Length > 1 ? proc[1] : null,
                            Charge = Fmt.ParseDecimal(s[2])
                        };
                        claim.Lines.Add(line);
                        break;
                }
            }

            if (result.Claims.Count == 0)
                result.Issues.Add(new Remittance835Issue(IssueSeverity.Warning, "No claim status (TRN*2) was found in the file."));
            return result;
        }
    }
}
