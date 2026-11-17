// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using EdiWeave.Hipaa835;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EdiWeave.UnitTests.Hipaa835
{
    [TestClass]
    public class RemittanceReaderTests
    {
        private static Remittance835Result ReadSample(string name)
        {
            var stream = typeof(RemittanceReaderTests).GetTypeInfo().Assembly
                .GetManifestResourceStream("EdiWeave.UnitTests.Hipaa835.Edi." + name);
            Assert.IsNotNull(stream, "Missing sample " + name);
            return Remittance835Reader.Read(stream);
        }

        [TestMethod]
        public void ReadsEnvelopePaymentAndParties()
        {
            var result = ReadSample("835_Balanced.txt");

            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            Assert.AreEqual("SYNPAYER", result.InterchangeSenderId);
            Assert.AreEqual("000000001", result.InterchangeControlNumber);
            Assert.AreEqual(1, result.Transactions.Count);

            var advice = result.Transactions[0];
            Assert.AreEqual("0001", advice.ControlNumber);
            Assert.AreEqual("005010X221A1", advice.ImplementationReference);
            Assert.AreEqual(215.5m, advice.Payment.TotalPayment);
            Assert.AreEqual("ACH", advice.Payment.PaymentMethod);
            Assert.AreEqual(new DateTime(2024, 10, 15), advice.Payment.PaymentDate);
            Assert.AreEqual("EFT0000123", advice.TraceNumber);
            Assert.AreEqual(new DateTime(2024, 10, 14), advice.ProductionDate);

            Assert.AreEqual("SYNTHETIC HEALTH INSURER", advice.Payer.Name);
            Assert.AreEqual("Hamilton", advice.Payer.City);
            Assert.AreEqual("BM", advice.Payer.CountryCode);
            Assert.AreEqual("PAYER01", advice.Payer.References.Single(r => r.Key == "2U").Value);

            Assert.AreEqual("SYNTHETIC CLINIC", advice.Payee.Name);
            Assert.AreEqual("1234567893", advice.Payee.Id);
            Assert.AreEqual(2, advice.Payee.AddressLines.Count);
            Assert.AreEqual("HM08", advice.Payee.PostalCode);
            Assert.AreEqual(41, advice.ActualSegmentCount);
            Assert.AreEqual(41, advice.DeclaredSegmentCount);
        }

        [TestMethod]
        public void ReadsClaimsServiceLinesAndAdjustments()
        {
            var advice = ReadSample("835_Balanced.txt").Transactions[0];

            Assert.AreEqual(3, advice.Claims.Count);

            var paid = advice.Claims[0];
            Assert.AreEqual("SYN-CLAIM-001", paid.PatientControlNumber);
            Assert.AreEqual("1", paid.StatusCode);
            Assert.AreEqual(200m, paid.TotalCharge);
            Assert.AreEqual(150m, paid.PaymentAmount);
            Assert.AreEqual(20m, paid.PatientResponsibility);
            Assert.AreEqual("PAYERCTRL01", paid.PayerClaimControlNumber);
            Assert.AreEqual("TESTPATIENT", paid.Patient.LastOrOrganizationName);
            Assert.AreEqual("SYN0001", paid.Patient.Id);
            Assert.AreEqual("1234567893", paid.RenderingProvider.Id);
            Assert.AreEqual(new DateTime(2024, 10, 5), paid.ReceivedDate);
            Assert.AreEqual(0, paid.Adjustments.Count);
            Assert.AreEqual(2, paid.ServiceLines.Count);

            var line1 = paid.ServiceLines[0];
            Assert.AreEqual("HC", line1.ProcedureQualifier);
            Assert.AreEqual("99214", line1.ProcedureCode);
            CollectionAssertEqual(new[] { "25" }, line1.Modifiers.ToArray());
            Assert.AreEqual(120m, line1.Charge);
            Assert.AreEqual(90m, line1.Paid);
            Assert.AreEqual(1m, line1.UnitsPaid);
            Assert.AreEqual(new DateTime(2024, 10, 1), line1.ServiceDate);
            Assert.AreEqual("SYN-CLAIM-0011", line1.LineItemControlNumber);
            Assert.AreEqual(105m, line1.AllowedAmount);
            Assert.AreEqual("N130", line1.RemarkCodes.Single().Value);
            Assert.AreEqual(2, line1.Adjustments.Count);
            Assert.AreEqual("CO", line1.Adjustments[0].GroupCode);
            Assert.AreEqual("45", line1.Adjustments[0].ReasonCode);
            Assert.AreEqual(15m, line1.Adjustments[0].Amount);
            Assert.AreEqual("PR", line1.Adjustments[1].GroupCode);

            // One CAS segment with two reasons becomes two adjustments in the same group.
            var line2 = paid.ServiceLines[1];
            Assert.AreEqual(1m, line2.UnitsPaid);
            Assert.AreEqual(3, line2.Adjustments.Count);
            Assert.AreEqual("253", line2.Adjustments[1].ReasonCode);
            Assert.AreEqual("CO", line2.Adjustments[1].GroupCode);
            Assert.AreEqual(5m, line2.Adjustments[1].Amount);
            Assert.IsNull(line2.Adjustments[1].Quantity);

            var denied = advice.Claims[1];
            Assert.AreEqual("4", denied.StatusCode);
            Assert.AreEqual(0m, denied.PaymentAmount);
            Assert.IsNull(denied.PatientResponsibility);
            Assert.AreEqual(0, denied.ServiceLines.Count);
            Assert.AreEqual("50", denied.Adjustments.Single().ReasonCode);
            Assert.AreEqual(new DateTime(2024, 9, 20), denied.StatementFrom);
            Assert.AreEqual(new DateTime(2024, 9, 20), denied.StatementTo);

            var deductible = advice.Claims[2];
            Assert.AreEqual("PR", deductible.Adjustments.Single().GroupCode);
            Assert.AreEqual(95m, deductible.Amounts.Single(a => a.Key == "AU").Value);
        }

        [TestMethod]
        public void ReadsProviderLevelAdjustments()
        {
            var advice = ReadSample("835_Balanced.txt").Transactions[0];

            Assert.AreEqual(2, advice.ProviderAdjustments.Count);
            var interest = advice.ProviderAdjustments[0];
            Assert.AreEqual("1234567893", interest.ProviderId);
            Assert.AreEqual(new DateTime(2024, 12, 31), interest.FiscalPeriodDate);
            Assert.AreEqual("L6", interest.ReasonCode);
            Assert.AreEqual("INT001", interest.ReferenceId);
            Assert.AreEqual(-2.5m, interest.Amount);
            Assert.AreEqual("WO", advice.ProviderAdjustments[1].ReasonCode);
            Assert.AreEqual(17m, advice.ProviderAdjustments[1].Amount);
        }

        [TestMethod]
        public void BalancedFilePassesBalancingChecks()
        {
            var advice = ReadSample("835_Balanced.txt").Transactions[0];
            var issues = RemittanceBalancing.Check(advice);
            Assert.AreEqual(0, issues.Count, string.Join("; ", issues));
        }

        [TestMethod]
        public void ReportsUnbalancedPaymentAndWrongSegmentCount()
        {
            var result = ReadSample("835_Unbalanced.txt");
            var advice = result.Transactions.Single();

            Assert.IsTrue(result.Issues.Any(i => i.Severity == IssueSeverity.Error && i.Message.StartsWith("SE01")),
                string.Join("; ", result.Issues));

            var issues = RemittanceBalancing.Check(advice);
            Assert.AreEqual(1, issues.Count, string.Join("; ", issues));
            Assert.IsTrue(issues[0].Message.StartsWith("Payment does not balance"), issues[0].Message);
            Assert.IsTrue(issues[0].Message.Contains("225.50"), issues[0].Message);
        }

        [TestMethod]
        public void ReadsNonPaddedIsaWithCustomDelimitersAndLineBreaks()
        {
            var result = ReadSample("835_CustomDelimiters.txt");

            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            Assert.AreEqual('|', result.Delimiters.Element);
            Assert.AreEqual('^', result.Delimiters.Component);
            Assert.AreEqual('{', result.Delimiters.Repetition);
            Assert.AreEqual('~', result.Delimiters.Segment);

            var advice = result.Transactions.Single();
            Assert.AreEqual(3, advice.Claims.Count);
            Assert.AreEqual("99214", advice.Claims[0].ServiceLines[0].ProcedureCode);
            Assert.AreEqual("25", advice.Claims[0].ServiceLines[0].Modifiers.Single());
            Assert.AreEqual("INT001", advice.ProviderAdjustments[0].ReferenceId);
            Assert.AreEqual(0, RemittanceBalancing.Check(advice).Count);
        }

        [TestMethod]
        public void ReadsMultipleTransactions()
        {
            var result = ReadSample("835_TwoTransactions.txt");

            Assert.AreEqual(0, result.Issues.Count, string.Join("; ", result.Issues));
            Assert.AreEqual(2, result.Transactions.Count);
            Assert.AreEqual("0001", result.Transactions[0].ControlNumber);
            Assert.AreEqual("0002", result.Transactions[1].ControlNumber);
            Assert.AreEqual(3, result.Transactions[1].Claims.Count);
            Assert.AreEqual("SYNPAYER", result.Transactions[1].GroupSenderCode);
        }

        [TestMethod]
        public void AmountsAreCultureInvariant()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                // Serbian and German use ',' as the decimal separator.
                foreach (var culture in new[] { "sr-Latn-RS", "de-DE", "tr-TR" })
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
                    var advice = ReadSample("835_Balanced.txt").Transactions[0];
                    Assert.AreEqual(215.5m, advice.Payment.TotalPayment, culture);
                    Assert.AreEqual(-2.5m, advice.ProviderAdjustments[0].Amount, culture);
                    Assert.AreEqual(0, RemittanceBalancing.Check(advice).Count, culture);
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [TestMethod]
        public void InvalidInputReportsIssueInsteadOfThrowing()
        {
            var result = Remittance835Reader.Read("this is not an X12 file");
            Assert.AreEqual(0, result.Transactions.Count);
            Assert.AreEqual(IssueSeverity.Error, result.Issues.Single().Severity);
        }

        [TestMethod]
        public void BadAmountIsReportedAndDoesNotLeakIntoPreviousClaim()
        {
            var text = new StreamReader(typeof(RemittanceReaderTests).GetTypeInfo().Assembly
                    .GetManifestResourceStream("EdiWeave.UnitTests.Hipaa835.Edi.835_Balanced.txt"))
                .ReadToEnd()
                .Replace("CLP*SYN-CLAIM-002*4*95*0*", "CLP*SYN-CLAIM-002*4*9X5*0*");

            var result = Remittance835Reader.Read(text);
            var advice = result.Transactions.Single();

            Assert.IsTrue(result.Issues.Any(i => i.Message.Contains("9X5")), string.Join("; ", result.Issues));
            Assert.AreEqual(2, advice.Claims.Count);
            // The CAS*CO*50 that belonged to the broken claim must not be added to claim 1.
            Assert.AreEqual(0, advice.Claims[0].Adjustments.Count);
        }

        [TestMethod]
        public void TruncatedFileReportsMissingTrailers()
        {
            var text = new StreamReader(typeof(RemittanceReaderTests).GetTypeInfo().Assembly
                    .GetManifestResourceStream("EdiWeave.UnitTests.Hipaa835.Edi.835_Balanced.txt"))
                .ReadToEnd();
            text = text.Substring(0, text.IndexOf("PLB*", StringComparison.Ordinal));

            var result = Remittance835Reader.Read(text);

            Assert.IsTrue(result.Issues.Any(i => i.Message.Contains("no SE trailer")), string.Join("; ", result.Issues));
            Assert.IsTrue(result.Issues.Any(i => i.Message.Contains("no IEA trailer")), string.Join("; ", result.Issues));
        }

        private static void CollectionAssertEqual(string[] expected, string[] actual)
        {
            Assert.AreEqual(string.Join("|", expected), string.Join("|", actual));
        }
    }
}
