// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;

namespace EdiWeave.Hipaa835
{
    /// <summary>Result of reading an 835 interchange.</summary>
    public sealed class Remittance835Result
    {
        /// <summary>Delimiters detected from the ISA segment, or null if no ISA was found.</summary>
        public X12Delimiters Delimiters { get; internal set; }

        /// <summary>ISA06 interchange sender ID (trimmed).</summary>
        public string InterchangeSenderId { get; internal set; }

        /// <summary>ISA08 interchange receiver ID (trimmed).</summary>
        public string InterchangeReceiverId { get; internal set; }

        /// <summary>ISA13 interchange control number.</summary>
        public string InterchangeControlNumber { get; internal set; }

        /// <summary>One entry per ST/SE transaction set.</summary>
        public List<RemittanceAdvice> Transactions { get; } = new List<RemittanceAdvice>();

        /// <summary>Structural problems found while reading (envelope, counts, unparsable values).</summary>
        public List<Remittance835Issue> Issues { get; } = new List<Remittance835Issue>();
    }

    /// <summary>Severity of an <see cref="Remittance835Issue"/>.</summary>
    public enum IssueSeverity
    {
        /// <summary>The data can be used, but something is unusual.</summary>
        Warning,

        /// <summary>The data is wrong or incomplete and should not be posted without review.</summary>
        Error
    }

    /// <summary>A problem found while reading or validating an 835.</summary>
    public sealed class Remittance835Issue
    {
        /// <summary>Creates an issue.</summary>
        public Remittance835Issue(IssueSeverity severity, string message, string transactionControlNumber = null,
            int? segmentIndex = null)
        {
            Severity = severity;
            Message = message;
            TransactionControlNumber = transactionControlNumber;
            SegmentIndex = segmentIndex;
        }

        /// <summary>Warning or error.</summary>
        public IssueSeverity Severity { get; }

        /// <summary>Human-readable description.</summary>
        public string Message { get; }

        /// <summary>ST02 of the transaction the issue belongs to, if any.</summary>
        public string TransactionControlNumber { get; }

        /// <summary>Zero-based index of the segment in the interchange, if known.</summary>
        public int? SegmentIndex { get; }

        /// <inheritdoc />
        public override string ToString() =>
            $"{Severity}: {Message}" + (TransactionControlNumber != null ? $" (ST {TransactionControlNumber})" : "") +
            (SegmentIndex.HasValue ? $" [segment {SegmentIndex}]" : "");
    }

    /// <summary>One 835 transaction set (ST..SE): a single payment and the claims it covers.</summary>
    public sealed class RemittanceAdvice
    {
        /// <summary>ST02 transaction set control number.</summary>
        public string ControlNumber { get; internal set; }

        /// <summary>ST03 implementation convention reference, normally 005010X221A1.</summary>
        public string ImplementationReference { get; internal set; }

        /// <summary>GS02 application sender code of the enclosing functional group.</summary>
        public string GroupSenderCode { get; internal set; }

        /// <summary>GS03 application receiver code of the enclosing functional group.</summary>
        public string GroupReceiverCode { get; internal set; }

        /// <summary>BPR financial information.</summary>
        public PaymentInfo Payment { get; } = new PaymentInfo();

        /// <summary>TRN02 check or EFT trace number.</summary>
        public string TraceNumber { get; internal set; }

        /// <summary>TRN03 payer identifier (originating company).</summary>
        public string TraceOriginatorId { get; internal set; }

        /// <summary>DTM*405 production date.</summary>
        public DateTime? ProductionDate { get; internal set; }

        /// <summary>Loop 1000A payer.</summary>
        public Party Payer { get; } = new Party();

        /// <summary>Loop 1000B payee.</summary>
        public Party Payee { get; } = new Party();

        /// <summary>Loop 2100 claims.</summary>
        public List<ClaimPayment> Claims { get; } = new List<ClaimPayment>();

        /// <summary>PLB provider-level adjustments.</summary>
        public List<ProviderAdjustment> ProviderAdjustments { get; } = new List<ProviderAdjustment>();

        /// <summary>SE01 segment count as sent.</summary>
        public int? DeclaredSegmentCount { get; internal set; }

        /// <summary>Segment count from ST to SE inclusive, as read.</summary>
        public int ActualSegmentCount { get; internal set; }

        /// <summary>Segments the reader does not map to the model, kept for inspection.</summary>
        public List<X12Segment> UnmappedSegments { get; } = new List<X12Segment>();
    }

    /// <summary>BPR segment.</summary>
    public sealed class PaymentInfo
    {
        /// <summary>BPR01: I = remittance with payment, H = notification only, etc.</summary>
        public string TransactionHandlingCode { get; internal set; }

        /// <summary>BPR02 total actual provider payment amount.</summary>
        public decimal TotalPayment { get; internal set; }

        /// <summary>BPR03: C = credit, D = debit.</summary>
        public string CreditDebitFlag { get; internal set; }

        /// <summary>BPR04: ACH, CHK, BOP, FWT, NON.</summary>
        public string PaymentMethod { get; internal set; }

        /// <summary>BPR05 payment format code.</summary>
        public string PaymentFormat { get; internal set; }

        /// <summary>BPR16 check issue or EFT effective date.</summary>
        public DateTime? PaymentDate { get; internal set; }
    }

    /// <summary>Payer or payee (N1 loop).</summary>
    public sealed class Party
    {
        /// <summary>N102 name.</summary>
        public string Name { get; internal set; }

        /// <summary>N103 identification code qualifier.</summary>
        public string IdQualifier { get; internal set; }

        /// <summary>N104 identification code.</summary>
        public string Id { get; internal set; }

        /// <summary>N301 and N302 address lines.</summary>
        public List<string> AddressLines { get; } = new List<string>();

        /// <summary>N401 city.</summary>
        public string City { get; internal set; }

        /// <summary>N402 state or province.</summary>
        public string State { get; internal set; }

        /// <summary>N403 postal code.</summary>
        public string PostalCode { get; internal set; }

        /// <summary>N404 country code.</summary>
        public string CountryCode { get; internal set; }

        /// <summary>REF segments, key = REF01 qualifier, value = REF02.</summary>
        public List<KeyValuePair<string, string>> References { get; } = new List<KeyValuePair<string, string>>();
    }

    /// <summary>Loop 2100: payment information for one claim (CLP).</summary>
    public sealed class ClaimPayment
    {
        /// <summary>CLP01 patient control number, as sent in the 837 CLM01.</summary>
        public string PatientControlNumber { get; internal set; }

        /// <summary>CLP02 claim status: 1 primary, 2 secondary, 3 tertiary, 4 denied, 19-23 forwarded/reversal etc.</summary>
        public string StatusCode { get; internal set; }

        /// <summary>CLP03 total claim charge amount.</summary>
        public decimal TotalCharge { get; internal set; }

        /// <summary>CLP04 claim payment amount.</summary>
        public decimal PaymentAmount { get; internal set; }

        /// <summary>CLP05 patient responsibility amount.</summary>
        public decimal? PatientResponsibility { get; internal set; }

        /// <summary>CLP06 claim filing indicator.</summary>
        public string FilingIndicator { get; internal set; }

        /// <summary>CLP07 payer claim control number.</summary>
        public string PayerClaimControlNumber { get; internal set; }

        /// <summary>CLP08 facility type code (place of service for professional claims).</summary>
        public string FacilityCode { get; internal set; }

        /// <summary>CLP09 claim frequency code.</summary>
        public string FrequencyCode { get; internal set; }

        /// <summary>Claim-level CAS adjustments.</summary>
        public List<Adjustment> Adjustments { get; } = new List<Adjustment>();

        /// <summary>NM1*QC patient.</summary>
        public PersonName Patient { get; internal set; }

        /// <summary>NM1*IL insured or subscriber.</summary>
        public PersonName Insured { get; internal set; }

        /// <summary>NM1*82 rendering provider.</summary>
        public PersonName RenderingProvider { get; internal set; }

        /// <summary>Other NM1 entities in the claim loop, keyed by NM101.</summary>
        public List<KeyValuePair<string, PersonName>> OtherEntities { get; } = new List<KeyValuePair<string, PersonName>>();

        /// <summary>DTM*232 claim statement period start.</summary>
        public DateTime? StatementFrom { get; internal set; }

        /// <summary>DTM*233 claim statement period end.</summary>
        public DateTime? StatementTo { get; internal set; }

        /// <summary>DTM*050 claim received date.</summary>
        public DateTime? ReceivedDate { get; internal set; }

        /// <summary>Claim-level REF segments.</summary>
        public List<KeyValuePair<string, string>> References { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Claim-level AMT segments, key = AMT01 qualifier.</summary>
        public List<KeyValuePair<string, decimal>> Amounts { get; } = new List<KeyValuePair<string, decimal>>();

        /// <summary>Loop 2110 service lines.</summary>
        public List<ServicePayment> ServiceLines { get; } = new List<ServicePayment>();

        /// <summary>Zero-based index of the CLP segment in the interchange.</summary>
        public int SegmentIndex { get; internal set; }
    }

    /// <summary>Loop 2110: payment information for one service line (SVC).</summary>
    public sealed class ServicePayment
    {
        /// <summary>SVC01-1 product/service ID qualifier, for example HC.</summary>
        public string ProcedureQualifier { get; internal set; }

        /// <summary>SVC01-2 procedure code.</summary>
        public string ProcedureCode { get; internal set; }

        /// <summary>SVC01-3..6 procedure modifiers.</summary>
        public List<string> Modifiers { get; } = new List<string>();

        /// <summary>SVC02 line item charge amount.</summary>
        public decimal Charge { get; internal set; }

        /// <summary>SVC03 line item provider payment amount.</summary>
        public decimal Paid { get; internal set; }

        /// <summary>SVC04 national uniform billing committee revenue code.</summary>
        public string RevenueCode { get; internal set; }

        /// <summary>SVC05 units of service paid; 1 when not sent.</summary>
        public decimal UnitsPaid { get; internal set; } = 1m;

        /// <summary>SVC06 original procedure code when the payer changed it, as sent.</summary>
        public string OriginalProcedure { get; internal set; }

        /// <summary>DTM*472 (or 150) service date.</summary>
        public DateTime? ServiceDate { get; internal set; }

        /// <summary>DTM*151 service period end.</summary>
        public DateTime? ServiceDateTo { get; internal set; }

        /// <summary>REF*6R line item control number, echoing the 837 REF*6R.</summary>
        public string LineItemControlNumber { get; internal set; }

        /// <summary>AMT*B6 allowed amount.</summary>
        public decimal? AllowedAmount { get; internal set; }

        /// <summary>Service-level CAS adjustments.</summary>
        public List<Adjustment> Adjustments { get; } = new List<Adjustment>();

        /// <summary>Service-level REF segments other than 6R.</summary>
        public List<KeyValuePair<string, string>> References { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>LQ remark codes, key = LQ01 qualifier (HE = RARC).</summary>
        public List<KeyValuePair<string, string>> RemarkCodes { get; } = new List<KeyValuePair<string, string>>();
    }

    /// <summary>One CAS adjustment (one reason within a CAS segment).</summary>
    public sealed class Adjustment
    {
        /// <summary>CAS01 group code: CO, PR, OA, PI or CR.</summary>
        public string GroupCode { get; internal set; }

        /// <summary>Claim adjustment reason code (CARC).</summary>
        public string ReasonCode { get; internal set; }

        /// <summary>Adjustment amount. Positive reduces payment.</summary>
        public decimal Amount { get; internal set; }

        /// <summary>Adjustment quantity, if sent.</summary>
        public decimal? Quantity { get; internal set; }
    }

    /// <summary>One PLB adjustment (one reason within a PLB segment).</summary>
    public sealed class ProviderAdjustment
    {
        /// <summary>PLB01 provider identifier.</summary>
        public string ProviderId { get; internal set; }

        /// <summary>PLB02 fiscal period date.</summary>
        public DateTime? FiscalPeriodDate { get; internal set; }

        /// <summary>Adjustment reason code, for example WO (withholding) or L6 (interest).</summary>
        public string ReasonCode { get; internal set; }

        /// <summary>Reference identifier from the reason composite, if sent.</summary>
        public string ReferenceId { get; internal set; }

        /// <summary>Amount. Positive reduces the payment; negative increases it.</summary>
        public decimal Amount { get; internal set; }
    }

    /// <summary>Name and identifier from an NM1 segment.</summary>
    public sealed class PersonName
    {
        /// <summary>NM102 entity type: 1 person, 2 non-person.</summary>
        public string EntityType { get; internal set; }

        /// <summary>NM103 last name or organization name.</summary>
        public string LastOrOrganizationName { get; internal set; }

        /// <summary>NM104 first name.</summary>
        public string FirstName { get; internal set; }

        /// <summary>NM105 middle name.</summary>
        public string MiddleName { get; internal set; }

        /// <summary>NM108 identification code qualifier.</summary>
        public string IdQualifier { get; internal set; }

        /// <summary>NM109 identification code.</summary>
        public string Id { get; internal set; }
    }
}
