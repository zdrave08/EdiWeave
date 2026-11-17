// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using EdiWeave.Hipaa835;

namespace EdiWeave.Healthcare
{
    /// <summary>Postal address.</summary>
    public sealed class Address
    {
        /// <summary>Street line 1 (N301).</summary>
        public string Line1 { get; set; }

        /// <summary>Street line 2 (N302).</summary>
        public string Line2 { get; set; }

        /// <summary>City (N401).</summary>
        public string City { get; set; }

        /// <summary>State or province code (N402).</summary>
        public string State { get; set; }

        /// <summary>Postal code (N403).</summary>
        public string PostalCode { get; set; }

        /// <summary>Country code (N404), only needed outside the United States.</summary>
        public string Country { get; set; }
    }

    /// <summary>An organization identified with a qualifier, used for the submitter and the receiver.</summary>
    public sealed class Organization
    {
        /// <summary>Organization name.</summary>
        public string Name { get; set; }

        /// <summary>Identification code qualifier, normally "46" (ETIN).</summary>
        public string IdQualifier { get; set; } = "46";

        /// <summary>Identification code.</summary>
        public string Id { get; set; }

        /// <summary>Contact name for the submitter (PER02).</summary>
        public string ContactName { get; set; }

        /// <summary>Contact telephone, digits only (PER04).</summary>
        public string ContactPhone { get; set; }
    }

    /// <summary>The provider that bills (loop 2010AA).</summary>
    public sealed class BillingProvider
    {
        /// <summary>Organization name, or last name when <see cref="IsPerson"/>.</summary>
        public string Name { get; set; }

        /// <summary>First name, only for an individual provider.</summary>
        public string FirstName { get; set; }

        /// <summary>True for an individual (solo practitioner), false for an organization.</summary>
        public bool IsPerson { get; set; }

        /// <summary>National Provider Identifier.</summary>
        public string Npi { get; set; }

        /// <summary>Taxonomy code (PRV03), optional.</summary>
        public string TaxonomyCode { get; set; }

        /// <summary>Employer identification number (REF*EI), 9 digits.</summary>
        public string Tin { get; set; }

        /// <summary>Billing address. Must be a street address, not a PO box.</summary>
        public Address Address { get; set; } = new Address();
    }

    /// <summary>The provider who rendered the service, when different from the billing provider (loop 2310B).</summary>
    public sealed class RenderingProvider
    {
        /// <summary>Last name.</summary>
        public string LastName { get; set; }

        /// <summary>First name.</summary>
        public string FirstName { get; set; }

        /// <summary>National Provider Identifier.</summary>
        public string Npi { get; set; }

        /// <summary>Taxonomy code, optional.</summary>
        public string TaxonomyCode { get; set; }
    }

    /// <summary>A person on a claim.</summary>
    public class Person
    {
        /// <summary>Last name.</summary>
        public string LastName { get; set; }

        /// <summary>First name.</summary>
        public string FirstName { get; set; }

        /// <summary>Middle name or initial.</summary>
        public string MiddleName { get; set; }

        /// <summary>Date of birth.</summary>
        public DateTime? DateOfBirth { get; set; }

        /// <summary>M, F or U.</summary>
        public string Gender { get; set; } = "U";

        /// <summary>Home address.</summary>
        public Address Address { get; set; } = new Address();
    }

    /// <summary>The policy holder (loop 2010BA).</summary>
    public sealed class Subscriber : Person
    {
        /// <summary>Member ID on the insurance card.</summary>
        public string MemberId { get; set; }

        /// <summary>Group number (SBR03), optional.</summary>
        public string GroupNumber { get; set; }

        /// <summary>Claim filing indicator (SBR09): CI commercial, MC Medicaid, MB Medicare Part B, and so on.</summary>
        public string ClaimFilingIndicator { get; set; } = "CI";

        /// <summary>Payer responsibility: P primary, S secondary, T tertiary.</summary>
        public string PayerResponsibility { get; set; } = "P";
    }

    /// <summary>A patient who is not the subscriber (loop 2010CA).</summary>
    public sealed class Dependent : Person
    {
        /// <summary>Relationship to the subscriber (PAT01): 01 spouse, 19 child, G8 other.</summary>
        public string RelationshipCode { get; set; } = "19";
    }

    /// <summary>The insurance payer (loop 2010BB).</summary>
    public sealed class Payer
    {
        /// <summary>Payer name.</summary>
        public string Name { get; set; }

        /// <summary>Payer identifier (NM109, qualifier PI).</summary>
        public string Id { get; set; }
    }

    /// <summary>One billed service (loop 2400).</summary>
    public sealed class ServiceLine
    {
        /// <summary>CPT or HCPCS code.</summary>
        public string ProcedureCode { get; set; }

        /// <summary>Up to four two-character modifiers.</summary>
        public List<string> Modifiers { get; } = new List<string>();

        /// <summary>Charge for the line (units times unit price).</summary>
        public decimal Charge { get; set; }

        /// <summary>Number of units.</summary>
        public decimal Units { get; set; } = 1m;

        /// <summary>Date of service.</summary>
        public DateTime ServiceDate { get; set; }

        /// <summary>1-based positions in <see cref="Claim837P.Diagnoses"/> that justify this line (1 to 4).</summary>
        public List<int> DiagnosisPointers { get; } = new List<int> { 1 };

        /// <summary>Your line identifier (REF*6R). It comes back in the 835 and links payments to lines.</summary>
        public string LineControlNumber { get; set; }
    }

    /// <summary>One professional claim (loop 2300).</summary>
    public sealed class Claim837P
    {
        /// <summary>Your claim identifier (CLM01). It comes back in the 277CA and the 835. Up to 20 characters; avoid patient identifiers.</summary>
        public string PatientControlNumber { get; set; }

        /// <summary>Total of the line charges.</summary>
        public decimal TotalCharge { get; set; }

        /// <summary>Place of service code, for example "11" for an office.</summary>
        public string PlaceOfService { get; set; } = "11";

        /// <summary>Claim frequency: 1 original, 7 replacement, 8 void.</summary>
        public string FrequencyCode { get; set; } = "1";

        /// <summary>Original payer claim number, required for replacement and void claims (REF*F8).</summary>
        public string OriginalPayerClaimNumber { get; set; }

        /// <summary>Prior authorization number (REF*G1), optional.</summary>
        public string PriorAuthorization { get; set; }

        /// <summary>Diagnosis codes without the dot. The first is the principal diagnosis. Up to 12.</summary>
        public List<string> Diagnoses { get; } = new List<string>();

        /// <summary>The policy holder.</summary>
        public Subscriber Subscriber { get; set; } = new Subscriber();

        /// <summary>The patient, when different from the subscriber.</summary>
        public Dependent Patient { get; set; }

        /// <summary>The payer.</summary>
        public Payer Payer { get; set; } = new Payer();

        /// <summary>Rendering provider, when different from the billing provider.</summary>
        public RenderingProvider Rendering { get; set; }

        /// <summary>Services, up to 50.</summary>
        public List<ServiceLine> ServiceLines { get; } = new List<ServiceLine>();
    }

    /// <summary>One file of 837P claims from a single billing provider.</summary>
    public sealed class Claim837PBatch
    {
        /// <summary>The submitter (loop 1000A).</summary>
        public Organization Submitter { get; set; } = new Organization();

        /// <summary>The receiver, normally the clearinghouse or payer (loop 1000B).</summary>
        public Organization Receiver { get; set; } = new Organization();

        /// <summary>The billing provider.</summary>
        public BillingProvider Billing { get; set; } = new BillingProvider();

        /// <summary>Batch identifier written to BHT03. Defaults to the group control number.</summary>
        public string BatchId { get; set; }

        /// <summary>The claims.</summary>
        public List<Claim837P> Claims { get; } = new List<Claim837P>();
    }

    /// <summary>Checks a batch for problems the payer would reject, before anything is sent.</summary>
    public static class Claim837PValidator
    {
        private static readonly string[] Genders = { "M", "F", "U" };

        /// <summary>Validates the batch. The result has no patient values in its messages.</summary>
        public static List<ValidationIssue> Validate(Claim837PBatch batch, DateTime? today = null)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            var issues = new List<ValidationIssue>();
            var now = (today ?? DateTime.Today).Date;

            void Error(string code, string path, string message) =>
                issues.Add(new ValidationIssue(IssueSeverity.Error, code, path, message));
            void Warn(string code, string path, string message) =>
                issues.Add(new ValidationIssue(IssueSeverity.Warning, code, path, message));

            if (string.IsNullOrWhiteSpace(batch.Submitter?.Name)) Error("SUBMITTER_NAME", "Submitter.Name", "Submitter name is required.");
            if (string.IsNullOrWhiteSpace(batch.Submitter?.Id)) Error("SUBMITTER_ID", "Submitter.Id", "Submitter ID is required.");
            if (string.IsNullOrWhiteSpace(batch.Receiver?.Name)) Error("RECEIVER_NAME", "Receiver.Name", "Receiver name is required.");
            if (string.IsNullOrWhiteSpace(batch.Receiver?.Id)) Error("RECEIVER_ID", "Receiver.Id", "Receiver ID is required.");

            var b = batch.Billing;
            if (b == null) Error("BILLING_MISSING", "Billing", "Billing provider is required.");
            else
            {
                if (string.IsNullOrWhiteSpace(b.Name)) Error("BILLING_NAME", "Billing.Name", "Billing provider name is required.");
                if (b.IsPerson && string.IsNullOrWhiteSpace(b.FirstName)) Error("BILLING_FIRST", "Billing.FirstName", "An individual billing provider needs a first name.");
                if (!Identifiers.IsValidNpi(b.Npi)) Error("NPI_INVALID", "Billing.Npi", "Billing NPI is not a valid NPI (10 digits with a correct check digit).");
                if (!Identifiers.IsValidTin(b.Tin)) Error("TIN_INVALID", "Billing.Tin", "Billing tax ID must be 9 digits.");
                CheckAddress(b.Address, "Billing.Address", true, Error, Warn);
                if (b.Address?.Line1 != null && b.Address.Line1.TrimStart().StartsWith("PO BOX", StringComparison.OrdinalIgnoreCase))
                    Error("BILLING_PO_BOX", "Billing.Address.Line1", "The billing provider address cannot be a PO box.");
            }

            if (batch.Claims.Count == 0) Error("NO_CLAIMS", "Claims", "The batch has no claims.");

            var pcns = new HashSet<string>();
            for (var i = 0; i < batch.Claims.Count; i++)
            {
                var c = batch.Claims[i];
                var p = $"Claims[{i}]";

                if (string.IsNullOrWhiteSpace(c.PatientControlNumber)) Error("PCN_MISSING", p + ".PatientControlNumber", "Patient control number is required.");
                else
                {
                    if (c.PatientControlNumber.Length > 20) Error("PCN_LENGTH", p + ".PatientControlNumber", "Patient control number is at most 20 characters.");
                    if (!pcns.Add(c.PatientControlNumber)) Error("PCN_DUPLICATE", p + ".PatientControlNumber", "Patient control number is used by another claim in this batch.");
                }

                if (c.ServiceLines.Count == 0) Error("NO_LINES", p + ".ServiceLines", "A claim needs at least one service line.");
                if (c.ServiceLines.Count > 50) Error("TOO_MANY_LINES", p + ".ServiceLines", "A professional claim has at most 50 service lines.");

                var lineTotal = c.ServiceLines.Sum(l => l.Charge);
                if (decimal.Round(c.TotalCharge, 2) != decimal.Round(lineTotal, 2))
                    Error("TOTAL_MISMATCH", p + ".TotalCharge", "Total charge does not equal the sum of the service line charges.");
                if (c.TotalCharge <= 0) Error("TOTAL_NOT_POSITIVE", p + ".TotalCharge", "Total charge must be greater than zero.");

                if (string.IsNullOrWhiteSpace(c.PlaceOfService) || c.PlaceOfService.Length != 2 || !c.PlaceOfService.All(char.IsDigit))
                    Error("POS_INVALID", p + ".PlaceOfService", "Place of service must be a two-digit code.");
                if (c.FrequencyCode != "1" && c.FrequencyCode != "7" && c.FrequencyCode != "8")
                    Error("FREQUENCY_INVALID", p + ".FrequencyCode", "Claim frequency must be 1, 7 or 8.");
                if ((c.FrequencyCode == "7" || c.FrequencyCode == "8") && string.IsNullOrWhiteSpace(c.OriginalPayerClaimNumber))
                    Error("ORIGINAL_CLAIM_MISSING", p + ".OriginalPayerClaimNumber", "A replacement or void claim needs the original payer claim number.");

                if (c.Diagnoses.Count == 0) Error("NO_DIAGNOSIS", p + ".Diagnoses", "At least one diagnosis is required.");
                if (c.Diagnoses.Count > 12) Error("TOO_MANY_DIAGNOSES", p + ".Diagnoses", "A claim has at most 12 diagnoses.");
                for (var d = 0; d < c.Diagnoses.Count; d++)
                    if (!Identifiers.IsValidIcd10Shape(c.Diagnoses[d]))
                        Error("ICD10_INVALID", $"{p}.Diagnoses[{d}]", "Diagnosis is not a valid ICD-10-CM code shape (no dot, letters in capitals).");
                if (c.Diagnoses.Distinct().Count() != c.Diagnoses.Count)
                    Warn("DIAGNOSIS_DUPLICATE", p + ".Diagnoses", "The same diagnosis is listed more than once.");

                CheckPerson(c.Subscriber, p + ".Subscriber", Error, Warn, now);
                if (c.Subscriber != null)
                {
                    if (string.IsNullOrWhiteSpace(c.Subscriber.MemberId)) Error("MEMBER_ID", p + ".Subscriber.MemberId", "Subscriber member ID is required.");
                    if (c.Subscriber.PayerResponsibility != "P" && c.Subscriber.PayerResponsibility != "S" && c.Subscriber.PayerResponsibility != "T")
                        Error("PAYER_RESP", p + ".Subscriber.PayerResponsibility", "Payer responsibility must be P, S or T.");
                    if (string.IsNullOrWhiteSpace(c.Subscriber.ClaimFilingIndicator)) Error("FILING_INDICATOR", p + ".Subscriber.ClaimFilingIndicator", "Claim filing indicator is required.");
                }
                if (c.Patient != null)
                {
                    CheckPerson(c.Patient, p + ".Patient", Error, Warn, now);
                    if (string.IsNullOrWhiteSpace(c.Patient.RelationshipCode)) Error("RELATIONSHIP", p + ".Patient.RelationshipCode", "Relationship code is required for a dependent patient.");
                    if (c.Patient.RelationshipCode == "18") Error("RELATIONSHIP_SELF", p + ".Patient.RelationshipCode", "Relationship 18 (self) means the patient is the subscriber; leave Patient empty instead.");
                }

                if (string.IsNullOrWhiteSpace(c.Payer?.Name)) Error("PAYER_NAME", p + ".Payer.Name", "Payer name is required.");
                if (string.IsNullOrWhiteSpace(c.Payer?.Id)) Error("PAYER_ID", p + ".Payer.Id", "Payer ID is required.");

                if (c.Rendering != null)
                {
                    if (!Identifiers.IsValidNpi(c.Rendering.Npi)) Error("NPI_INVALID", p + ".Rendering.Npi", "Rendering NPI is not a valid NPI.");
                    if (string.IsNullOrWhiteSpace(c.Rendering.LastName)) Error("RENDERING_NAME", p + ".Rendering.LastName", "Rendering provider last name is required.");
                }

                var lineIds = new HashSet<string>();
                for (var l = 0; l < c.ServiceLines.Count; l++)
                {
                    var line = c.ServiceLines[l];
                    var lp = $"{p}.ServiceLines[{l}]";
                    if (!Identifiers.IsValidProcedureCodeShape(line.ProcedureCode))
                        Error("PROCEDURE_INVALID", lp + ".ProcedureCode", "Procedure code must be a CPT or HCPCS code (5 characters).");
                    if (line.Modifiers.Count > 4) Error("MODIFIERS", lp + ".Modifiers", "At most four modifiers.");
                    if (line.Modifiers.Any(m => m == null || m.Length != 2))
                        Error("MODIFIER_INVALID", lp + ".Modifiers", "Each modifier is two characters.");
                    if (line.Charge < 0) Error("CHARGE_NEGATIVE", lp + ".Charge", "Charge cannot be negative.");
                    if (line.Units <= 0) Error("UNITS", lp + ".Units", "Units must be greater than zero.");
                    if (line.ServiceDate == default(DateTime)) Error("SERVICE_DATE", lp + ".ServiceDate", "Service date is required.");
                    else if (line.ServiceDate.Date > now) Error("SERVICE_DATE_FUTURE", lp + ".ServiceDate", "Service date is in the future.");
                    if (line.DiagnosisPointers.Count == 0 || line.DiagnosisPointers.Count > 4)
                        Error("POINTERS", lp + ".DiagnosisPointers", "A line needs one to four diagnosis pointers.");
                    if (line.DiagnosisPointers.Any(x => x < 1 || x > c.Diagnoses.Count))
                        Error("POINTER_RANGE", lp + ".DiagnosisPointers", "A diagnosis pointer refers to a diagnosis that does not exist.");
                    if (string.IsNullOrWhiteSpace(line.LineControlNumber))
                        Warn("LINE_CONTROL", lp + ".LineControlNumber", "Without a line control number the 835 cannot be matched to this line reliably.");
                    else if (!lineIds.Add(line.LineControlNumber))
                        Error("LINE_CONTROL_DUPLICATE", lp + ".LineControlNumber", "Line control number is used twice in this claim.");
                }
            }

            return issues;
        }

        private static void CheckPerson(Person person, string path, Action<string, string, string> error,
            Action<string, string, string> warn, DateTime today)
        {
            if (person == null)
            {
                error("PERSON_MISSING", path, "Person is required.");
                return;
            }
            if (string.IsNullOrWhiteSpace(person.LastName)) error("LAST_NAME", path + ".LastName", "Last name is required.");
            if (string.IsNullOrWhiteSpace(person.FirstName)) error("FIRST_NAME", path + ".FirstName", "First name is required.");
            if (!person.DateOfBirth.HasValue) error("DOB_MISSING", path + ".DateOfBirth", "Date of birth is required.");
            else if (person.DateOfBirth.Value.Date > today) error("DOB_FUTURE", path + ".DateOfBirth", "Date of birth is in the future.");
            if (!Genders.Contains(person.Gender ?? string.Empty)) error("GENDER", path + ".Gender", "Gender must be M, F or U.");
            CheckAddress(person.Address, path + ".Address", true, error, warn);
        }

        private static void CheckAddress(Address a, string path, bool required, Action<string, string, string> error,
            Action<string, string, string> warn)
        {
            if (a == null || (string.IsNullOrWhiteSpace(a.Line1) && string.IsNullOrWhiteSpace(a.City)))
            {
                if (required) error("ADDRESS_MISSING", path, "Address is required.");
                return;
            }
            if (string.IsNullOrWhiteSpace(a.Line1)) error("ADDRESS_LINE", path + ".Line1", "Street address is required.");
            if (string.IsNullOrWhiteSpace(a.City)) error("ADDRESS_CITY", path + ".City", "City is required.");
            if (string.IsNullOrWhiteSpace(a.State) || a.State.Length != 2) error("ADDRESS_STATE", path + ".State", "State must be a two-letter code.");
            var zip = Fmt.Digits(a.PostalCode);
            if (zip.Length != 5 && zip.Length != 9) error("ADDRESS_ZIP", path + ".PostalCode", "Postal code must have 5 or 9 digits.");
        }

        /// <summary>True when the list has no errors (warnings are allowed).</summary>
        public static bool IsValid(IEnumerable<ValidationIssue> issues) => issues.All(i => i.Severity != IssueSeverity.Error);
    }

    /// <summary>Writes HIPAA 5010 837P (005010X222A1) professional claim files.</summary>
    public static class Claim837PWriter
    {
        /// <summary>The implementation guide reference written to ST03 and GS08.</summary>
        public const string ImplementationReference = "005010X222A1";

        /// <summary>
        /// Writes the batch as X12. Throws <see cref="ClaimValidationException"/> when validation finds errors,
        /// unless <paramref name="skipValidation"/> is true.
        /// </summary>
        public static string Write(Claim837PBatch batch, X12Options options, bool skipValidation = false)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (!skipValidation)
            {
                var issues = Claim837PValidator.Validate(batch);
                if (!Claim837PValidator.IsValid(issues)) throw new ClaimValidationException(issues);
            }

            var d = new X12Document(options, "HC", "837", ImplementationReference);
            var created = options.Timestamp ?? DateTime.Now;
            var c = d.Comp;

            d.Seg("BHT", "0019", "00", batch.BatchId ?? options.GroupControlNumber.ToString(),
                Fmt.Date(created), created.ToString("HHmm"), "CH");

            d.Seg("NM1", "41", "2", batch.Submitter.Name, "", "", "", "", batch.Submitter.IdQualifier ?? "46", batch.Submitter.Id);
            d.Seg("PER", "IC", batch.Submitter.ContactName ?? batch.Submitter.Name,
                string.IsNullOrWhiteSpace(batch.Submitter.ContactPhone) ? "" : "TE",
                Fmt.Digits(batch.Submitter.ContactPhone));
            d.Seg("NM1", "40", "2", batch.Receiver.Name, "", "", "", "", batch.Receiver.IdQualifier ?? "46", batch.Receiver.Id);

            var hl = 1;
            d.Seg("HL", "1", "", "20", "1");
            var billing = batch.Billing;
            if (!string.IsNullOrWhiteSpace(billing.TaxonomyCode)) d.Seg("PRV", "BI", "PXC", billing.TaxonomyCode);
            d.Seg("NM1", "85", billing.IsPerson ? "1" : "2", billing.Name, billing.IsPerson ? billing.FirstName : "", "", "", "", "XX", billing.Npi);
            d.Seg("N3", billing.Address.Line1, billing.Address.Line2);
            d.Seg("N4", billing.Address.City, billing.Address.State, Fmt.Digits(billing.Address.PostalCode), billing.Address.Country);
            d.Seg("REF", "EI", Fmt.Digits(billing.Tin));

            var parentOfBilling = hl;
            var groups = batch.Claims
                .GroupBy(x => new { Payer = x.Payer.Id, Member = x.Subscriber.MemberId, Resp = x.Subscriber.PayerResponsibility })
                .ToList();

            foreach (var group in groups)
            {
                var first = group.First();
                var sub = first.Subscriber;
                var dependents = group.Where(x => x.Patient != null)
                    .GroupBy(x => PatientKey(x.Patient)).ToList();
                var ownClaims = group.Where(x => x.Patient == null).ToList();

                hl++;
                var subscriberHl = hl;
                d.Seg("HL", hl.ToString(), parentOfBilling.ToString(), "22", dependents.Count > 0 ? "1" : "0");
                d.Seg("SBR", sub.PayerResponsibility, dependents.Count == 0 || ownClaims.Count > 0 ? "18" : "",
                    sub.GroupNumber, "", "", "", "", "", sub.ClaimFilingIndicator);
                d.Seg("NM1", "IL", "1", sub.LastName, sub.FirstName, sub.MiddleName, "", "", "MI", sub.MemberId);
                WriteAddress(d, sub.Address);
                d.Seg("DMG", "D8", Fmt.Date(sub.DateOfBirth.Value), sub.Gender);
                d.Seg("NM1", "PR", "2", first.Payer.Name, "", "", "", "", "PI", first.Payer.Id);

                foreach (var claim in ownClaims) WriteClaim(d, claim);

                foreach (var dep in dependents)
                {
                    var patient = dep.First().Patient;
                    hl++;
                    d.Seg("HL", hl.ToString(), subscriberHl.ToString(), "23", "0");
                    d.Seg("PAT", patient.RelationshipCode);
                    d.Seg("NM1", "QC", "1", patient.LastName, patient.FirstName, patient.MiddleName);
                    WriteAddress(d, patient.Address);
                    d.Seg("DMG", "D8", Fmt.Date(patient.DateOfBirth.Value), patient.Gender);
                    foreach (var claim in dep) WriteClaim(d, claim);
                }
            }

            return d.Build();
        }

        private static string PatientKey(Dependent p) =>
            string.Join("|", p.LastName, p.FirstName, p.DateOfBirth?.ToString("yyyyMMdd"), p.RelationshipCode);

        private static void WriteAddress(X12Document d, Address a)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Line1)) return;
            d.Seg("N3", a.Line1, a.Line2);
            d.Seg("N4", a.City, a.State, Fmt.Digits(a.PostalCode), a.Country);
        }

        private static void WriteClaim(X12Document d, Claim837P claim)
        {
            d.Seg("CLM", claim.PatientControlNumber, Fmt.Amount(claim.TotalCharge), "", "",
                d.Comp(claim.PlaceOfService, "B", claim.FrequencyCode), "Y", "A", "Y", "Y");
            if (!string.IsNullOrWhiteSpace(claim.OriginalPayerClaimNumber)) d.Seg("REF", "F8", claim.OriginalPayerClaimNumber);
            if (!string.IsNullOrWhiteSpace(claim.PriorAuthorization)) d.Seg("REF", "G1", claim.PriorAuthorization);

            var hi = new List<string> { "HI" };
            for (var i = 0; i < claim.Diagnoses.Count; i++)
                hi.Add(d.Comp(i == 0 ? "ABK" : "ABF", claim.Diagnoses[i]));
            d.Seg(hi.ToArray());

            if (claim.Rendering != null)
            {
                d.Seg("NM1", "82", "1", claim.Rendering.LastName, claim.Rendering.FirstName, "", "", "", "XX", claim.Rendering.Npi);
                if (!string.IsNullOrWhiteSpace(claim.Rendering.TaxonomyCode)) d.Seg("PRV", "PE", "PXC", claim.Rendering.TaxonomyCode);
            }

            var lx = 0;
            foreach (var line in claim.ServiceLines)
            {
                lx++;
                var parts = new List<string> { "HC", line.ProcedureCode };
                parts.AddRange(line.Modifiers);
                d.Seg("LX", lx.ToString());
                d.Seg("SV1", d.Comp(parts.ToArray()), Fmt.Amount(line.Charge), "UN",
                    line.Units.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "", "",
                    d.Comp(line.DiagnosisPointers.Select(x => x.ToString()).ToArray()));
                d.Seg("DTP", "472", "D8", Fmt.Date(line.ServiceDate));
                if (!string.IsNullOrWhiteSpace(line.LineControlNumber)) d.Seg("REF", "6R", line.LineControlNumber);
            }
        }
    }

    /// <summary>Thrown by <see cref="Claim837PWriter.Write"/> when the batch has validation errors.</summary>
    public sealed class ClaimValidationException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public ClaimValidationException(IReadOnlyList<ValidationIssue> issues)
            : base("The claim batch has validation errors: " + string.Join("; ",
                issues.Where(i => i.Severity == IssueSeverity.Error).Take(5).Select(i => i.Code + " at " + i.Path))
                + (issues.Count(i => i.Severity == IssueSeverity.Error) > 5 ? "; ..." : ""))
        {
            Issues = issues;
        }

        /// <summary>All issues, warnings included.</summary>
        public IReadOnlyList<ValidationIssue> Issues { get; }
    }
}
