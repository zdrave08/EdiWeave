// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using EdiWeave.Hipaa835;

namespace EdiWeave.Healthcare
{
    /// <summary>A request for eligibility and benefits (270, 005010X279A1) for one subscriber.</summary>
    public sealed class EligibilityRequest
    {
        /// <summary>The payer (loop 2100A).</summary>
        public Payer Payer { get; set; } = new Payer();

        /// <summary>Provider name asking (loop 2100B).</summary>
        public string ProviderName { get; set; }

        /// <summary>Provider NPI.</summary>
        public string ProviderNpi { get; set; }

        /// <summary>Subscriber member ID.</summary>
        public string MemberId { get; set; }

        /// <summary>Subscriber last name.</summary>
        public string LastName { get; set; }

        /// <summary>Subscriber first name.</summary>
        public string FirstName { get; set; }

        /// <summary>Subscriber date of birth.</summary>
        public DateTime? DateOfBirth { get; set; }

        /// <summary>Date of service the eligibility is checked for. Defaults to today.</summary>
        public DateTime? DateOfService { get; set; }

        /// <summary>Your trace number (TRN02). It comes back in the 271 so you can match the answer.</summary>
        public string TraceNumber { get; set; }

        /// <summary>Service type codes asked for (EQ01). 30 is "health benefit plan coverage" and is the default.</summary>
        public List<string> ServiceTypeCodes { get; } = new List<string> { "30" };
    }

    /// <summary>Writes 270 eligibility requests.</summary>
    public static class Eligibility270Writer
    {
        /// <summary>The implementation guide reference written to ST03 and GS08.</summary>
        public const string ImplementationReference = "005010X279A1";

        /// <summary>Checks the request. Messages never contain patient values.</summary>
        public static List<ValidationIssue> Validate(EligibilityRequest r)
        {
            var issues = new List<ValidationIssue>();
            void Error(string code, string path, string message) =>
                issues.Add(new ValidationIssue(IssueSeverity.Error, code, path, message));

            if (string.IsNullOrWhiteSpace(r.Payer?.Name)) Error("PAYER_NAME", "Payer.Name", "Payer name is required.");
            if (string.IsNullOrWhiteSpace(r.Payer?.Id)) Error("PAYER_ID", "Payer.Id", "Payer ID is required.");
            if (string.IsNullOrWhiteSpace(r.ProviderName)) Error("PROVIDER_NAME", "ProviderName", "Provider name is required.");
            if (!Identifiers.IsValidNpi(r.ProviderNpi)) Error("NPI_INVALID", "ProviderNpi", "Provider NPI is not a valid NPI.");
            if (string.IsNullOrWhiteSpace(r.MemberId)) Error("MEMBER_ID", "MemberId", "Member ID is required.");
            if (string.IsNullOrWhiteSpace(r.LastName)) Error("LAST_NAME", "LastName", "Last name is required.");
            if (string.IsNullOrWhiteSpace(r.FirstName)) Error("FIRST_NAME", "FirstName", "First name is required.");
            if (r.ServiceTypeCodes.Count == 0) Error("SERVICE_TYPE", "ServiceTypeCodes", "At least one service type code is required.");
            return issues;
        }

        /// <summary>Writes the 270. Throws <see cref="ClaimValidationException"/> when the request is invalid.</summary>
        public static string Write(EligibilityRequest request, X12Options options)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (options == null) throw new ArgumentNullException(nameof(options));
            var issues = Validate(request);
            if (issues.Any(i => i.Severity == IssueSeverity.Error)) throw new ClaimValidationException(issues);

            var d = new X12Document(options, "HS", "270", ImplementationReference);
            var created = options.Timestamp ?? DateTime.Now;
            var trace = request.TraceNumber ?? options.GroupControlNumber.ToString();

            d.Seg("BHT", "0022", "13", trace, Fmt.Date(created), created.ToString("HHmm"));
            d.Seg("HL", "1", "", "20", "1");
            d.Seg("NM1", "PR", "2", request.Payer.Name, "", "", "", "", "PI", request.Payer.Id);
            d.Seg("HL", "2", "1", "21", "1");
            d.Seg("NM1", "1P", "2", request.ProviderName, "", "", "", "", "XX", request.ProviderNpi);
            d.Seg("HL", "3", "2", "22", "0");
            d.Seg("TRN", "1", trace, "9" + Fmt.Digits(request.ProviderNpi));
            d.Seg("NM1", "IL", "1", request.LastName, request.FirstName, "", "", "", "MI", request.MemberId);
            if (request.DateOfBirth.HasValue) d.Seg("DMG", "D8", Fmt.Date(request.DateOfBirth.Value));
            d.Seg("DTP", "291", "D8", Fmt.Date((request.DateOfService ?? created).Date));
            foreach (var code in request.ServiceTypeCodes) d.Seg("EQ", code);

            return d.Build();
        }
    }

    /// <summary>One benefit line of a 271 (EB segment).</summary>
    public sealed class EligibilityBenefit
    {
        /// <summary>EB01 eligibility or benefit information code: 1 active, 6 inactive, A co-insurance, B co-payment, C deductible, G out-of-pocket, and so on.</summary>
        public string InfoCode { get; internal set; }

        /// <summary>EB02 coverage level: IND individual, FAM family, EMP employee only, and so on.</summary>
        public string CoverageLevel { get; internal set; }

        /// <summary>EB03 service type codes the benefit applies to.</summary>
        public List<string> ServiceTypes { get; } = new List<string>();

        /// <summary>EB04 insurance type code.</summary>
        public string InsuranceType { get; internal set; }

        /// <summary>EB05 plan coverage description.</summary>
        public string PlanDescription { get; internal set; }

        /// <summary>EB06 time period qualifier: 23 calendar year, 25 contract, 27 visit, 29 remaining, and so on.</summary>
        public string TimePeriod { get; internal set; }

        /// <summary>EB07 monetary amount (co-payment, deductible, limit).</summary>
        public decimal? Amount { get; internal set; }

        /// <summary>EB08 percent as written in the file (0.2 means 20 percent).</summary>
        public decimal? Percent { get; internal set; }

        /// <summary>EB11 authorization or certification required: Y, N or U.</summary>
        public string AuthorizationRequired { get; internal set; }

        /// <summary>EB12 in-plan network indicator: Y in network, N out of network, W not applicable.</summary>
        public string InNetwork { get; internal set; }

        /// <summary>The loop the benefit was in: 22 subscriber or 23 dependent.</summary>
        public string Level { get; internal set; }
    }

    /// <summary>A request-level or loop-level rejection (AAA segment).</summary>
    public sealed class EligibilityRejection
    {
        /// <summary>HL03 of the loop the rejection belongs to: 20 payer, 21 provider, 22 subscriber, 23 dependent.</summary>
        public string Level { get; internal set; }

        /// <summary>AAA03 reject reason code, for example 72 (invalid or missing subscriber ID), 75 (subscriber not found).</summary>
        public string ReasonCode { get; internal set; }

        /// <summary>AAA04 follow-up action code: C correct and resubmit, N resubmission not allowed, R resubmit later, S do not resubmit.</summary>
        public string FollowUpAction { get; internal set; }
    }

    /// <summary>A 271 answer.</summary>
    public sealed class EligibilityResponse
    {
        /// <summary>TRN02 of the answer: the trace number of your request.</summary>
        public string TraceNumber { get; internal set; }

        /// <summary>Payer name (NM1*PR).</summary>
        public string PayerName { get; internal set; }

        /// <summary>Provider NPI the answer is for (NM1*1P).</summary>
        public string ProviderNpi { get; internal set; }

        /// <summary>Member ID returned for the subscriber (NM1*IL).</summary>
        public string MemberId { get; internal set; }

        /// <summary>Benefit lines.</summary>
        public List<EligibilityBenefit> Benefits { get; } = new List<EligibilityBenefit>();

        /// <summary>Rejections.</summary>
        public List<EligibilityRejection> Rejections { get; } = new List<EligibilityRejection>();

        /// <summary>True when the payer rejected the request (no usable answer).</summary>
        public bool HasRejections => Rejections.Count > 0;

        /// <summary>True when coverage is active and none of the benefit lines says inactive.</summary>
        public bool IsActive =>
            Benefits.Any(b => b.InfoCode == "1" || b.InfoCode == "2" || b.InfoCode == "3" || b.InfoCode == "4" || b.InfoCode == "5") &&
            !IsInactive;

        /// <summary>True when a benefit line says inactive (6, 7 or 8).</summary>
        public bool IsInactive => Benefits.Any(b => b.InfoCode == "6" || b.InfoCode == "7" || b.InfoCode == "8");

        /// <summary>First co-payment amount for the service type, or the general one when <paramref name="serviceType"/> is null.</summary>
        public decimal? CoPayment(string serviceType = null) => FirstAmount("B", serviceType);

        /// <summary>First deductible amount.</summary>
        public decimal? Deductible(string serviceType = null) => FirstAmount("C", serviceType);

        /// <summary>First out-of-pocket (stop loss) amount.</summary>
        public decimal? OutOfPocket(string serviceType = null) => FirstAmount("G", serviceType);

        /// <summary>First co-insurance percent as a fraction (0.2 means 20 percent).</summary>
        public decimal? CoInsurance(string serviceType = null) =>
            For("A", serviceType).Select(b => b.Percent).FirstOrDefault(p => p.HasValue);

        private decimal? FirstAmount(string code, string serviceType) =>
            For(code, serviceType).Select(b => b.Amount).FirstOrDefault(a => a.HasValue);

        private IEnumerable<EligibilityBenefit> For(string code, string serviceType) =>
            Benefits.Where(b => b.InfoCode == code &&
                                (serviceType == null || b.ServiceTypes.Count == 0 || b.ServiceTypes.Contains(serviceType)));
    }

    /// <summary>Reads 271 eligibility responses.</summary>
    public static class EligibilityReader
    {
        /// <summary>Reads every 271 transaction in the text. Never throws on bad content.</summary>
        public static List<EligibilityResponse> Read(string text, List<Remittance835Issue> issues = null)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var responses = new List<EligibilityResponse>();
            IReadOnlyList<X12Segment> segments;
            X12Delimiters delimiters;
            try { segments = X12Tokenizer.Tokenize(text, out delimiters); }
            catch (FormatException ex)
            {
                issues?.Add(new Remittance835Issue(IssueSeverity.Error, ex.Message));
                return responses;
            }

            EligibilityResponse current = null;
            string level = null;

            foreach (var s in segments)
            {
                switch (s.Id)
                {
                    case "ST":
                        current = s[1] == "271" ? new EligibilityResponse() : null;
                        if (current != null) responses.Add(current);
                        level = null;
                        break;
                    case "HL":
                        level = s[3];
                        break;
                    case "TRN" when current != null && s[1] == "2":
                        current.TraceNumber = s[2];
                        break;
                    case "NM1" when current != null:
                        if (s[1] == "PR") current.PayerName = s[3];
                        else if (s[1] == "1P") current.ProviderNpi = s[9];
                        else if (s[1] == "IL") current.MemberId = s[9];
                        break;
                    case "AAA" when current != null:
                        current.Rejections.Add(new EligibilityRejection
                        {
                            Level = level,
                            ReasonCode = s[3],
                            FollowUpAction = s[4]
                        });
                        break;
                    case "EB" when current != null:
                        var benefit = new EligibilityBenefit
                        {
                            InfoCode = s[1],
                            CoverageLevel = s[2],
                            InsuranceType = s[4],
                            PlanDescription = s[5],
                            TimePeriod = s[6],
                            Amount = Fmt.ParseDecimal(s[7]),
                            Percent = Fmt.ParseDecimal(s[8]),
                            AuthorizationRequired = s[11],
                            InNetwork = s[12],
                            Level = level
                        };
                        if (!string.IsNullOrEmpty(s[3]))
                        {
                            var separator = delimiters.Repetition ?? delimiters.Component;
                            benefit.ServiceTypes.AddRange(s[3].Split(separator).Where(x => x.Length > 0));
                        }
                        current.Benefits.Add(benefit);
                        break;
                }
            }

            return responses;
        }
    }
}
