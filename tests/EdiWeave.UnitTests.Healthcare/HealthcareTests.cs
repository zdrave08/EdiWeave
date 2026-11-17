// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EdiWeave.Core.Model.Edi;
using EdiWeave.Core.Model.Edi.ErrorContexts;
using EdiWeave.Core.Model.Edi.X12;
using EdiWeave.Framework;
using EdiWeave.Framework.Readers;
using EdiWeave.Healthcare;
using EdiWeave.Hipaa835;
using EdiWeave.Rules.HIPAA_005010X222A1_837;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EdiWeave.UnitTests.Healthcare
{
    [TestClass]
    public class Claim837PTests
    {
        private static Assembly Rules(MessageContext mc) => Assembly.Load(new AssemblyName("EdiWeave.Rules.Hipaa005010"));

        // Rules.Hipaa005010 ships a demo validator ("CustomValidationSegment") that flags every loop 2000A.
        // It is not a claim problem, so it is ignored here.
        private static void AssertNoRuleErrors(MessageErrorContext ec)
        {
            var errors = ec == null
                ? new List<string>()
                : ec.Errors.Where(e => e.Name != "CustomValidationSegment").Select(e => e.Name + " " + e.Message).ToList();
            Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        }

        private static string[] Segments(string x12) => x12.Split('~').Where(s => s.Length > 0).ToArray();

        [TestMethod]
        public void NpiCheckDigit()
        {
            Assert.IsTrue(Identifiers.IsValidNpi("1234567893"));
            Assert.IsTrue(Identifiers.IsValidNpi("1245319599"));
            Assert.IsFalse(Identifiers.IsValidNpi("1234567890"));
            Assert.IsFalse(Identifiers.IsValidNpi("123456789"));
            Assert.IsFalse(Identifiers.IsValidNpi("3234567893"));
            Assert.IsFalse(Identifiers.IsValidNpi("12345678AB"));
            Assert.IsFalse(Identifiers.IsValidNpi(null));
        }

        [TestMethod]
        public void CodeShapes()
        {
            Assert.IsTrue(Identifiers.IsValidIcd10Shape("J069"));
            Assert.IsTrue(Identifiers.IsValidIcd10Shape("S72001A"));
            Assert.IsFalse(Identifiers.IsValidIcd10Shape("J06.9"));
            Assert.IsFalse(Identifiers.IsValidIcd10Shape("j069"));
            Assert.IsFalse(Identifiers.IsValidIcd10Shape("U071"));
            Assert.IsTrue(Identifiers.IsValidProcedureCodeShape("99214"));
            Assert.IsTrue(Identifiers.IsValidProcedureCodeShape("G0008"));
            Assert.IsTrue(Identifiers.IsValidProcedureCodeShape("0001F"));
            Assert.IsFalse(Identifiers.IsValidProcedureCodeShape("9921"));
            Assert.IsFalse(Identifiers.IsValidProcedureCodeShape("Z1234"));
        }

        [TestMethod]
        public void ValidBatchHasNoErrors()
        {
            var issues = Claim837PValidator.Validate(TestData.Batch(), new DateTime(2026, 11, 17));
            Assert.IsTrue(Claim837PValidator.IsValid(issues), string.Join("; ", issues.Select(i => i.ToString())));
        }

        [TestMethod]
        public void ValidatorFindsProblemsAndNeverEchoesPatientValues()
        {
            var batch = TestData.Batch();
            var claim = batch.Claims[0];
            claim.TotalCharge = 999m;
            claim.Diagnoses[0] = "J06.9";
            claim.ServiceLines[0].DiagnosisPointers[0] = 7;
            claim.ServiceLines[1].LineControlNumber = claim.ServiceLines[0].LineControlNumber;
            claim.ServiceLines[1].ServiceDate = new DateTime(2030, 1, 1);
            claim.Subscriber.DateOfBirth = new DateTime(2031, 1, 1);
            claim.Subscriber.Address.PostalCode = "12";
            batch.Billing.Npi = "1234567890";
            batch.Billing.Address.Line1 = "PO BOX 5";

            var issues = Claim837PValidator.Validate(batch, new DateTime(2026, 11, 17));
            var codes = issues.Select(i => i.Code).ToList();

            foreach (var expected in new[] { "TOTAL_MISMATCH", "ICD10_INVALID", "POINTER_RANGE", "LINE_CONTROL_DUPLICATE",
                         "SERVICE_DATE_FUTURE", "DOB_FUTURE", "ADDRESS_ZIP", "NPI_INVALID", "BILLING_PO_BOX" })
                CollectionAssert.Contains(codes, expected);

            var text = string.Join(" ", issues.Select(i => i.Message));
            Assert.IsFalse(text.Contains("TESTPATIENT"));
            Assert.IsFalse(text.Contains("SYN0001"));
        }

        [TestMethod]
        public void ReplacementClaimNeedsOriginalNumber()
        {
            var batch = TestData.Batch();
            batch.Claims[0].FrequencyCode = "7";
            CollectionAssert.Contains(Claim837PValidator.Validate(batch, new DateTime(2026, 11, 17)).Select(i => i.Code).ToList(), "ORIGINAL_CLAIM_MISSING");
        }

        [TestMethod]
        public void WriterRefusesInvalidBatch()
        {
            var batch = TestData.Batch();
            batch.Claims[0].Diagnoses.Clear();
            var ex = Assert.ThrowsException<ClaimValidationException>(() => Claim837PWriter.Write(batch, TestData.Options()));
            Assert.IsTrue(ex.Issues.Any(i => i.Code == "NO_DIAGNOSIS"));
        }

        [TestMethod]
        public void WritesAWellFormedInterchange()
        {
            var x12 = Claim837PWriter.Write(TestData.Batch(), TestData.Options(icn: 42, gcn: 7));
            var segs = Segments(x12);

            Assert.IsTrue(segs[0].StartsWith("ISA*00*          *00*          *ZZ*SYNCLINIC      *ZZ*SYNHOUSE       *261117*1030*^*00501*000000042*0*T*:"));
            Assert.AreEqual(105, segs[0].Length, "ISA is fixed width");
            Assert.AreEqual("GS*HC*SYNCLINIC*SYNHOUSE*20261117*1030*7*X*005010X222A1", segs[1]);
            Assert.AreEqual("ST*837*0001*005010X222A1", segs[2]);
            Assert.AreEqual("GE*1*7", segs[segs.Length - 2]);
            Assert.AreEqual("IEA*1*000000042", segs[segs.Length - 1]);

            // SE01 counts ST..SE inclusive.
            var st = Array.FindIndex(segs, s => s.StartsWith("ST*"));
            var se = Array.FindIndex(segs, s => s.StartsWith("SE*"));
            Assert.AreEqual("SE*" + (se - st + 1) + "*0001", segs[se]);

            CollectionAssert.Contains(segs, "NM1*85*2*SYNTHETIC CLINIC*****XX*1234567893");
            CollectionAssert.Contains(segs, "REF*EI*123456789");
            CollectionAssert.Contains(segs, "PRV*BI*PXC*207Q00000X");
            CollectionAssert.Contains(segs, "CLM*CLAIM-001*200***11:B:1*Y*A*Y*Y");
            CollectionAssert.Contains(segs, "HI*ABK:J069*ABF:R5081");
            CollectionAssert.Contains(segs, "SV1*HC:99214:25*120*UN*1***1");
            CollectionAssert.Contains(segs, "DTP*472*D8*20261103");
            CollectionAssert.Contains(segs, "REF*6R*CLAIM-001-2");
            CollectionAssert.Contains(segs, "DMG*D8*19800517*F");
            Assert.IsTrue(segs.Any(s => s.StartsWith("PER*IC*BILLING OFFICE*TE*4415550100")));
            // PRV comes right after the billing provider HL, before its NM1.
            Assert.IsTrue(Array.IndexOf(segs, "PRV*BI*PXC*207Q00000X") < Array.IndexOf(segs, "NM1*85*2*SYNTHETIC CLINIC*****XX*1234567893"));
        }

        [TestMethod]
        public void OutputIsAcceptedByTheRulesEngine()
        {
            var x12 = Claim837PWriter.Write(TestData.Batch(), TestData.Options());
            List<EdiItem> items;
            using (var reader = new X12Reader(new MemoryStream(Encoding.UTF8.GetBytes(x12)), Rules))
                items = reader.ReadToEnd().ToList();

            Assert.IsNull(items.OfType<ReaderErrorContext>().FirstOrDefault()?.Exception?.Message);
            var ts = items.OfType<TS837>().Single();
            MessageErrorContext ec;
            ts.IsValid(out ec);
            AssertNoRuleErrors(ec);
        }

        [TestMethod]
        public void DependentAndSecondClaimShareOneSubscriberLoop()
        {
            var batch = TestData.Batch();
            var second = TestData.Claim("CLAIM-002", "TESTPATIENT", "ALPHA", "SYN0001");
            var third = TestData.Claim("CLAIM-003", "TESTPATIENT", "ALPHA", "SYN0001");
            third.Patient = new Dependent
            {
                LastName = "TESTPATIENT",
                FirstName = "KID",
                DateOfBirth = new DateTime(2015, 2, 2),
                Gender = "M",
                RelationshipCode = "19",
                Address = new Address { Line1 = "1 EXAMPLE STREET", City = "HAMILTON", State = "HM", PostalCode = "12345" }
            };
            batch.Claims.Add(second);
            batch.Claims.Add(third);

            var x12 = Claim837PWriter.Write(batch, TestData.Options());
            var segs = Segments(x12);

            Assert.AreEqual(1, segs.Count(s => s.StartsWith("HL*") && s.Contains("*22*")), "one subscriber loop");
            Assert.AreEqual(1, segs.Count(s => s.Contains("*23*")), "one patient loop");
            Assert.AreEqual(3, segs.Count(s => s.StartsWith("CLM*")));
            var sub = segs.First(s => s.StartsWith("HL*") && s.Contains("*22*"));
            Assert.IsTrue(sub.EndsWith("*1"), "subscriber loop has a child");
            CollectionAssert.Contains(segs, "PAT*19");
            CollectionAssert.Contains(segs, "NM1*QC*1*TESTPATIENT*KID");

            List<EdiItem> items;
            using (var reader = new X12Reader(new MemoryStream(Encoding.UTF8.GetBytes(x12)), Rules))
                items = reader.ReadToEnd().ToList();
            Assert.IsNull(items.OfType<ReaderErrorContext>().FirstOrDefault()?.Exception?.Message);
            MessageErrorContext ec;
            items.OfType<TS837>().Single().IsValid(out ec);
            AssertNoRuleErrors(ec);
        }

        [TestMethod]
        public void DelimitersInDataAreRemoved()
        {
            var batch = TestData.Batch();
            batch.Claims[0].Subscriber.LastName = "O*BRIEN~JR";
            var x12 = Claim837PWriter.Write(batch, TestData.Options());
            CollectionAssert.Contains(Segments(x12), "NM1*IL*1*O BRIEN JR*ALPHA****MI*SYN0001");
        }

        [TestMethod]
        public void RoundTripThroughTheReaderKeepsControlNumbers()
        {
            var x12 = Claim837PWriter.Write(TestData.Batch(), TestData.Options(icn: 5, gcn: 9));
            var segments = X12Tokenizer.Tokenize(x12, out var delimiters);
            Assert.AreEqual('~', delimiters.Segment);
            Assert.AreEqual("000000005", segments.First(s => s.Id == "ISA")[13]);
            Assert.AreEqual("9", segments.First(s => s.Id == "GS")[6]);
            Assert.AreEqual(EdiKind.Claim837, EdiClassifier.Classify(x12));
        }

        [TestMethod]
        public void OptionsAreChecked()
        {
            var options = TestData.Options();
            options.SenderId = null;
            Assert.ThrowsException<InvalidOperationException>(() => Claim837PWriter.Write(TestData.Batch(), options));
            options = TestData.Options();
            options.ComponentSeparator = '*';
            Assert.ThrowsException<InvalidOperationException>(() => Claim837PWriter.Write(TestData.Batch(), options));
        }
    }

    [TestClass]
    public class AcknowledgementTests
    {
        [TestMethod]
        public void Reads999Accepted()
        {
            var r = AckReader.Read(TestData.Ack999Accepted);
            var ack = r.ImplementationAcks.Single();
            Assert.AreEqual("1", ack.GroupControlNumber);
            Assert.AreEqual("A", ack.GroupStatus);
            Assert.IsTrue(ack.IsAccepted);
            Assert.AreEqual(1, ack.SetsAccepted);
            Assert.IsTrue(ack.TransactionSets.Single().IsAccepted);
        }

        [TestMethod]
        public void Reads999RejectedWithErrors()
        {
            var ack = AckReader.Read(TestData.Ack999Rejected).ImplementationAcks.Single();
            Assert.AreEqual("R", ack.GroupStatus);
            Assert.IsFalse(ack.IsAccepted);
            var set = ack.TransactionSets.Single();
            Assert.AreEqual("R", set.Status);
            CollectionAssert.AreEqual(new[] { "5" }, set.ErrorCodes);
            var error = set.Errors.Single();
            Assert.AreEqual("NM1", error.SegmentId);
            Assert.AreEqual(7, error.SegmentPosition);
            Assert.AreEqual("2010BA", error.LoopId);
            Assert.AreEqual("8", error.SegmentErrorCode);
            Assert.AreEqual(3, error.ElementPosition);
            Assert.AreEqual("7", error.ElementErrorCode);
        }

        [TestMethod]
        public void ReadsTa1()
        {
            var ta1 = AckReader.Read(TestData.Ta1Rejected).InterchangeAcks.Single();
            Assert.AreEqual("000000003", ta1.InterchangeControlNumber);
            Assert.AreEqual("R", ta1.AckCode);
            Assert.AreEqual("023", ta1.NoteCode);
            Assert.IsFalse(ta1.IsAccepted);
            Assert.AreEqual(EdiKind.InterchangeAckTa1, EdiClassifier.Classify(TestData.Ta1Rejected));
        }

        [TestMethod]
        public void Reads277CA()
        {
            var result = ClaimStatusReader.Read(TestData.Claim277);
            Assert.AreEqual(2, result.Claims.Count);

            var accepted = result.Claims[0];
            Assert.AreEqual("CLAIM-001", accepted.PatientControlNumber);
            Assert.AreEqual("PAYERCTRL01", accepted.PayerClaimControlNumber);
            Assert.AreEqual("CH0001", accepted.ClearinghouseTraceNumber);
            Assert.AreEqual(ClaimStatusOutcome.Accepted, accepted.Outcome);
            Assert.AreEqual(200m, accepted.Statuses[0].Amount);
            Assert.AreEqual("20", accepted.Statuses[0].Code);

            var rejected = result.Claims[1];
            Assert.AreEqual("CLAIM-002", rejected.PatientControlNumber);
            Assert.AreEqual(ClaimStatusOutcome.Rejected, rejected.Outcome);
            Assert.AreEqual(2, rejected.Statuses.Count);
            Assert.AreEqual("187", rejected.Statuses[0].Code);
            Assert.AreEqual(EdiKind.ClaimStatus277, EdiClassifier.Classify(TestData.Claim277));
        }

        [TestMethod]
        public void BadInputDoesNotThrow()
        {
            Assert.AreEqual(1, AckReader.Read("not edi").Issues.Count);
            Assert.AreEqual(1, ClaimStatusReader.Read("not edi").Issues.Count);
            Assert.AreEqual(EdiKind.Unknown, EdiClassifier.Classify("not edi"));
            Assert.AreEqual(EdiKind.Unknown, EdiClassifier.Classify(null));
        }
    }

    [TestClass]
    public class EligibilityTests
    {
        private static EligibilityRequest Request()
        {
            return new EligibilityRequest
            {
                Payer = new Payer { Name = "SYNTHETIC HEALTH INSURER", Id = "PAYER01" },
                ProviderName = "SYNTHETIC CLINIC",
                ProviderNpi = TestData.BillingNpi,
                MemberId = "SYN0001",
                LastName = "TESTPATIENT",
                FirstName = "ALPHA",
                DateOfBirth = new DateTime(1980, 5, 17),
                DateOfService = new DateTime(2026, 11, 18),
                TraceNumber = "TRACE77"
            };
        }

        [TestMethod]
        public void Writes270()
        {
            var x12 = Eligibility270Writer.Write(Request(), TestData.Options());
            var segs = x12.Split('~').Where(s => s.Length > 0).ToArray();

            Assert.AreEqual("GS*HS*SYNCLINIC*SYNHOUSE*20261117*1030*1*X*005010X279A1", segs[1]);
            Assert.AreEqual("ST*270*0001*005010X279A1", segs[2]);
            CollectionAssert.Contains(segs, "BHT*0022*13*TRACE77*20261117*1030");
            CollectionAssert.Contains(segs, "NM1*PR*2*SYNTHETIC HEALTH INSURER*****PI*PAYER01");
            CollectionAssert.Contains(segs, "NM1*1P*2*SYNTHETIC CLINIC*****XX*1234567893");
            CollectionAssert.Contains(segs, "TRN*1*TRACE77*91234567893");
            CollectionAssert.Contains(segs, "NM1*IL*1*TESTPATIENT*ALPHA****MI*SYN0001");
            CollectionAssert.Contains(segs, "DMG*D8*19800517");
            CollectionAssert.Contains(segs, "DTP*291*D8*20261118");
            CollectionAssert.Contains(segs, "EQ*30");
            var st = Array.FindIndex(segs, s => s.StartsWith("ST*"));
            var se = Array.FindIndex(segs, s => s.StartsWith("SE*"));
            Assert.AreEqual("SE*" + (se - st + 1) + "*0001", segs[se]);
            Assert.AreEqual(EdiKind.Eligibility270, EdiClassifier.Classify(x12));
        }

        [TestMethod]
        public void Rejects270WithBadNpi()
        {
            var r = Request();
            r.ProviderNpi = "1234567890";
            Assert.ThrowsException<ClaimValidationException>(() => Eligibility270Writer.Write(r, TestData.Options()));
        }

        [TestMethod]
        public void Reads271()
        {
            var response = EligibilityReader.Read(TestData.Eligibility271).Single();
            Assert.AreEqual("TRACE77", response.TraceNumber);
            Assert.AreEqual("SYN0001", response.MemberId);
            Assert.AreEqual(TestData.BillingNpi, response.ProviderNpi);
            Assert.IsTrue(response.IsActive);
            Assert.IsFalse(response.IsInactive);
            Assert.IsFalse(response.HasRejections);
            Assert.AreEqual(25m, response.CoPayment("98"));
            Assert.AreEqual(500m, response.Deductible("30"));
            Assert.AreEqual(3000m, response.OutOfPocket());
            Assert.AreEqual(0.2m, response.CoInsurance("98"));
            CollectionAssert.AreEqual(new[] { "30", "98" }, response.Benefits[0].ServiceTypes);
            Assert.AreEqual("GOLD PLAN", response.Benefits[0].PlanDescription);
            Assert.AreEqual(EdiKind.Eligibility271, EdiClassifier.Classify(TestData.Eligibility271));
        }

        [TestMethod]
        public void Reads271Rejection()
        {
            var response = EligibilityReader.Read(TestData.Eligibility271Rejected).Single();
            Assert.IsTrue(response.HasRejections);
            Assert.IsFalse(response.IsActive);
            var rejection = response.Rejections.Single();
            Assert.AreEqual("22", rejection.Level);
            Assert.AreEqual("75", rejection.ReasonCode);
            Assert.AreEqual("C", rejection.FollowUpAction);
        }
    }

    [TestClass]
    public class ReconciliationTests
    {
        [TestMethod]
        public void MatchesPaymentsAndFlagsProblems()
        {
            var claims = new[]
            {
                TestData.Claim("CLAIM-001", "TESTPATIENT", "ALPHA", "SYN0001"),
                TestData.Claim("CLAIM-002", "TESTPATIENT", "BETA", "SYN0002"),
                TestData.Claim("CLAIM-003", "TESTPATIENT", "GAMMA", "SYN0003")
            };
            var remit = Remittance835Reader.Read(TestData.Remittance835).Transactions;
            var statuses = ClaimStatusReader.Read(TestData.Claim277).Claims;

            var result = ClaimReconciler.Reconcile(claims, remit, statuses);

            var paid = result.Claims[0];
            Assert.AreEqual(ClaimOutcome.Paid, paid.Outcome);
            Assert.AreEqual(130m, paid.Paid);
            Assert.AreEqual(20m, paid.PatientResponsibility);
            Assert.AreEqual(70m, paid.Adjustments);
            Assert.AreEqual("PAYERCTRL01", paid.PayerClaimControlNumber);
            Assert.AreEqual(2, paid.Lines.Count(l => l.MatchedInRemittance));
            Assert.AreEqual(90m, paid.Lines[0].Paid);
            Assert.AreEqual(40m, paid.Lines[1].Paid);
            Assert.AreEqual(0, paid.Notes.Count, string.Join("; ", paid.Notes));

            var denied = result.Claims[1];
            Assert.AreEqual(ClaimOutcome.Denied, denied.Outcome);
            Assert.AreEqual(0m, denied.Paid);
            Assert.IsTrue(denied.Notes.Any(n => n.Contains("different total charge")), "payer lists 95, claim sent 200");

            var open = result.Claims[2];
            Assert.AreEqual(ClaimOutcome.Submitted, open.Outcome);

            Assert.AreEqual(1, result.UnmatchedPayments.Count);
            Assert.AreEqual("CLAIM-999", result.UnmatchedPayments[0].PatientControlNumber);
            Assert.AreEqual(130m, result.TotalPaid);
        }

        [TestMethod]
        public void A277RejectionWithoutPaymentIsRejected()
        {
            var claim = TestData.Claim("CLAIM-002", "TESTPATIENT", "BETA", "SYN0002");
            var result = ClaimReconciler.Reconcile(new[] { claim }, null, ClaimStatusReader.Read(TestData.Claim277).Claims);
            Assert.AreEqual(ClaimOutcome.Rejected, result.Claims[0].Outcome);
        }

        [TestMethod]
        public void AcceptedWithoutPaymentStaysAccepted()
        {
            var claim = TestData.Claim("CLAIM-001", "TESTPATIENT", "ALPHA", "SYN0001");
            var result = ClaimReconciler.Reconcile(new[] { claim }, null, ClaimStatusReader.Read(TestData.Claim277).Claims);
            Assert.AreEqual(ClaimOutcome.Accepted, result.Claims[0].Outcome);
            Assert.AreEqual("PAYERCTRL01", result.Claims[0].PayerClaimControlNumber);
        }
    }

    [TestClass]
    public class TransportTests
    {
        private string _root;

        [TestInitialize]
        public void Init() => _root = Path.Combine(Path.GetTempPath(), "ediweave-" + Guid.NewGuid().ToString("N"));

        [TestCleanup]
        public void Clean()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private FolderTransport Transport() =>
            new FolderTransport(Path.Combine(_root, "out"), Path.Combine(_root, "in"), Path.Combine(_root, "done"));

        [TestMethod]
        public async System.Threading.Tasks.Task FolderTransportSendsReceivesAndMarksProcessed()
        {
            var t = Transport();
            await t.SendAsync("a.x12", Encoding.UTF8.GetBytes("hello"));
            Assert.IsTrue(File.Exists(Path.Combine(_root, "out", "a.x12")));
            Assert.IsFalse(File.Exists(Path.Combine(_root, "out", "a.x12.tmp")));
            await Assert.ThrowsExceptionAsync<IOException>(() => t.SendAsync("a.x12", new byte[1]));
            await t.SendAsync("sub/../evil.x12", new byte[1]);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "evil.x12")), "a path in the name never leaves the outbox");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "out", "evil.x12")));
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => t.SendAsync("", new byte[1]));

            File.WriteAllText(Path.Combine(_root, "in", "r1.txt"), "response");
            var files = await t.ReceiveAsync();
            Assert.AreEqual(1, files.Count);
            Assert.AreEqual("response", files[0].Text);
            await t.MarkProcessedAsync(files[0]);
            Assert.AreEqual(0, (await t.ReceiveAsync()).Count);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "done", "r1.txt")));
        }

        [TestMethod]
        public async System.Threading.Tasks.Task SubmitThenProcessAcknowledgement()
        {
            var transport = Transport();
            var log = new SubmissionLog(Path.Combine(_root, "log", "submissions.json"));
            var audit = new AuditLog(Path.Combine(_root, "log", "audit.log"));
            var submitter = new ClaimSubmitter(transport, log, audit);

            var result = await submitter.SubmitAsync(TestData.Batch(), TestData.Options(icn: 3, gcn: 1), "tester");
            Assert.AreEqual("837P_000000003.x12", result.FileName);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "out", result.FileName)));
            var record = log.ReadAll().Single();
            Assert.AreEqual(SubmissionStatus.Sent, record.Status);
            Assert.AreEqual(1, record.ClaimCount);
            Assert.AreEqual(200m, record.TotalCharge);
            Assert.AreEqual(64, record.Sha256.Length);
            Assert.IsFalse(File.ReadAllText(Path.Combine(_root, "log", "submissions.json")).Contains("TESTPATIENT"), "the log holds no patient data");

            // The same control number cannot be used twice.
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => submitter.SubmitAsync(TestData.Batch(), TestData.Options(icn: 3, gcn: 2), "tester"));

            // The clearinghouse answers with a 999 for group 1, and a file nobody knows.
            File.WriteAllText(Path.Combine(_root, "in", "999.edi"), TestData.Ack999Accepted);
            File.WriteAllText(Path.Combine(_root, "in", "other.txt"), "garbage");
            var responses = await submitter.ProcessResponsesAsync("tester");

            Assert.AreEqual(1, responses.Acknowledgements.Count);
            Assert.AreEqual(1, responses.SubmissionsUpdated);
            CollectionAssert.AreEqual(new[] { "other.txt" }, responses.UnknownFiles);
            Assert.AreEqual(SubmissionStatus.Acknowledged, log.ReadAll().Single().Status);
            Assert.IsTrue(File.Exists(Path.Combine(_root, "in", "other.txt")), "unknown files stay in the inbox");
            Assert.IsTrue(File.Exists(Path.Combine(_root, "done", "999.edi")));
            Assert.IsTrue(audit.Verify());
            Assert.IsTrue(audit.ReadAll().Any(e => e.Action == "claims.submit" && e.Outcome == "sent"));
        }

        [TestMethod]
        public async System.Threading.Tasks.Task InvalidBatchIsNeverSent()
        {
            var transport = Transport();
            var log = new SubmissionLog(Path.Combine(_root, "log", "submissions.json"));
            var submitter = new ClaimSubmitter(transport, log);
            var batch = TestData.Batch();
            batch.Billing.Npi = "1";

            await Assert.ThrowsExceptionAsync<ClaimValidationException>(() => submitter.SubmitAsync(batch, TestData.Options(), "tester"));
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_root, "out")).Length);
            Assert.AreEqual(0, log.ReadAll().Count);
        }

        [TestMethod]
        public void Ta1RejectionUpdatesTheLog()
        {
            var log = new SubmissionLog(Path.Combine(_root, "log", "submissions.json"));
            log.Add(new SubmissionRecord { FileName = "x", InterchangeControlNumber = 3, GroupControlNumber = 1 });
            Assert.AreEqual(1, log.Apply(AckReader.Read(TestData.Ta1Rejected)));
            var r = log.ReadAll().Single();
            Assert.AreEqual(SubmissionStatus.RejectedAtFileLevel, r.Status);
            StringAssert.Contains(r.Note, "023");
        }
    }

    [TestClass]
    public class SecurityTests
    {
        private string _file;

        [TestInitialize]
        public void Init() => _file = Path.Combine(Path.GetTempPath(), "ediweave-audit-" + Guid.NewGuid().ToString("N") + ".log");

        [TestCleanup]
        public void Clean()
        {
            if (File.Exists(_file)) File.Delete(_file);
        }

        [TestMethod]
        public void AuditLogDetectsTampering()
        {
            var audit = new AuditLog(_file, () => new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));
            audit.Record("alice", "claims.submit", "file1", "sent", "1 claims");
            audit.Record("alice", "responses.read", "file2", "processed", null);
            audit.Record("bob", "claims.submit", "file3", "sent", "3 claims");
            Assert.IsTrue(audit.Verify());
            Assert.AreEqual(3, audit.ReadAll().Count);

            // Change a value.
            var lines = File.ReadAllLines(_file);
            File.WriteAllLines(_file, new[] { lines[0].Replace("alice", "mallory"), lines[1], lines[2] });
            Assert.IsFalse(audit.Verify(), "edited line");

            // Remove the middle line.
            File.WriteAllLines(_file, new[] { lines[0], lines[2] });
            Assert.IsFalse(audit.Verify(), "removed line");

            // Original again.
            File.WriteAllLines(_file, lines);
            Assert.IsTrue(audit.Verify());
        }

        [TestMethod]
        public void RedactorMasksPatientDataButKeepsStructure()
        {
            var x12 = Claim837PWriter.Write(TestData.Batch(), TestData.Options());
            var redacted = PhiRedactor.Redact(x12);

            foreach (var secret in new[] { "TESTPATIENT", "ALPHA", "SYN0001", "1 EXAMPLE STREET", "HAMILTON", "19800517", "CLAIM-001", "GRP100" })
                Assert.IsFalse(redacted.Contains(secret), secret + " should be masked");

            // Structure and non-patient identifiers are kept.
            StringAssert.Contains(redacted, "NM1*85*2*SYNTHETIC CLINIC*****XX*1234567893");
            StringAssert.Contains(redacted, "DMG*D8*19800101*F");
            StringAssert.Contains(redacted, "SV1*HC:99214:25*120*UN*1***1");
            StringAssert.Contains(redacted, "HI*ABK:J069*ABF:R5081");
            Assert.AreEqual(x12.Count(c => c == '~'), redacted.Count(c => c == '~'));
            Assert.AreEqual("XXXX", PhiRedactor.Redact("garbage that is not x12"));
        }

        [TestMethod]
        public void EncryptionRoundTripAndTamperDetection()
        {
            var data = Encoding.UTF8.GetBytes("ISA*00*...synthetic");
            var encrypted = EdiFileProtector.Encrypt(data, "correct horse battery staple");
            CollectionAssert.AreNotEqual(data, encrypted);
            CollectionAssert.AreEqual(data, EdiFileProtector.Decrypt(encrypted, "correct horse battery staple"));

            Assert.ThrowsException<AuthenticationTagMismatchException>(() => EdiFileProtector.Decrypt(encrypted, "a different long password"));

            var tampered = (byte[])encrypted.Clone();
            tampered[tampered.Length - 1] ^= 1;
            Assert.ThrowsException<AuthenticationTagMismatchException>(() => EdiFileProtector.Decrypt(tampered, "correct horse battery staple"));
            Assert.ThrowsException<CryptographicException>(() => EdiFileProtector.Decrypt(new byte[10], "correct horse battery staple"));
            Assert.ThrowsException<ArgumentException>(() => EdiFileProtector.Encrypt(data, "short"));

            // The same plaintext never gives the same file twice.
            CollectionAssert.AreNotEqual(encrypted, EdiFileProtector.Encrypt(data, "correct horse battery staple"));
        }

        [TestMethod]
        public void EncryptionWithAKey()
        {
            var key = new byte[32];
            for (var i = 0; i < key.Length; i++) key[i] = (byte)i;
            var data = Encoding.UTF8.GetBytes("remittance");
            var encrypted = EdiFileProtector.Encrypt(data, key);
            CollectionAssert.AreEqual(data, EdiFileProtector.Decrypt(encrypted, key));
            Assert.ThrowsException<ArgumentException>(() => EdiFileProtector.Encrypt(data, new byte[16]));
        }
    }
}
