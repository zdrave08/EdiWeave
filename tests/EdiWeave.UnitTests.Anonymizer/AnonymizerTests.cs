// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Linq;
using System.Text;
using EdiWeave.Anonymizer;
using EdiWeave.Hipaa835;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EdiWeave.UnitTests.Anonymizer
{
    [TestClass]
    public class AnonymizerTests
    {
        // Synthetic data only. The layout mirrors a Medisoft 5010 837P; every name and number is invented.
        private const string Claim837 =
            "ISA*00*          *00*          *ZZ*99999          *ZZ*321            *241015*1409*+*00501*000000080*0*P*:~" +
            "GS*HC*99999*321*20241015*1409*80*X*005010X222A1~" +
            "ST*837*0080*005010X222A1~" +
            "BHT*0019*00*000080*20241015*1409*CH~" +
            "NM1*41*2*SYNTHETIC CLINIC*****46*99999~" +
            "PER*IC*SYNTHETIC CLINIC*TE*4415550100~" +
            "NM1*40*2*RECEIVER*****46*321~" +
            "HL*1**20*1~" +
            "NM1*85*2*SYNTHETIC CLINIC*****XX*1234567893~" +
            "N3*1 Clinic Road~" +
            "N4*Pembroke*BM*00000~" +
            "HL*2*1*22*0~" +
            "SBR*P*18*123-4******CI~" +
            "NM1*IL*1*FAKESURNAME*FAKEGIVEN*Q***MI*123-4-56789~" +
            "N3*P. O. Box TEST 999~" +
            "N4*Paget*BM*00000*BE~" +
            "DMG*D8*19700315*F~" +
            "NM1*PR*2*Synthetic Insurance Co.*****PI*12345~" +
            "CLM*4711*95***11:B:1*Y*A*Y*Y~" +
            "REF*D9*4711~" +
            "HI*ABK:E119*ABF:E785~" +
            "NTE*ADD*CALL FAKEGIVEN FAKESURNAME AFTER VISIT~" +
            "NM1*82*1*Doctorson*Dana*A***XX*1234567893~" +
            "LX*1~" +
            "SV1*HC:97803*95*UN*1***1:2~" +
            "DTP*472*RD8*20240410-20240412~" +
            "REF*6R*47111~" +
            "SE*26*0080~" +
            "GE*1*80~" +
            "IEA*1*000000080~";

        private const string Remit835 =
            "ISA*00*          *00*          *ZZ*SYNPAYER       *ZZ*SYNCLINIC      *241101*1200*^*00501*000000001*0*P*>~" +
            "GS*HP*SYNPAYER*SYNCLINIC*20241101*1200*1*X*005010X221A1~" +
            "ST*835*0001*005010X221A1~" +
            "BPR*I*80*C*CHK************20241101~" +
            "TRN*1*CHK001*1999999999~" +
            "N1*PR*Synthetic Insurance Co.~" +
            "N1*PE*SYNTHETIC CLINIC*XX*1234567893~" +
            "LX*1~" +
            "CLP*4711*1*95*80*15*12*PAYERCLAIM99*11*1~" +
            "NM1*QC*1*FAKESURNAME*FAKEGIVEN****MI*123-4-56789~" +
            "SVC*HC>97803*95*80**1~" +
            "DTM*472*20240410~" +
            "CAS*PR*1*15~" +
            "REF*6R*47111~" +
            "SE*13*0001~" +
            "GE*1*1~" +
            "IEA*1*000000001~";

        private static X12Anonymizer Create(string key = "test-key-0123456789") =>
            new X12Anonymizer(Encoding.UTF8.GetBytes(key));

        private static string Element(string edi, string segmentStart, int position, char element = '*')
        {
            var segment = edi.Split('~').First(s => s.StartsWith(segmentStart, StringComparison.Ordinal));
            return segment.Split(element)[position];
        }

        [TestMethod]
        public void RemovesPatientData()
        {
            var result = Create().Anonymize(Claim837);

            Assert.AreEqual(0, result.Leaks.Count, string.Join(",", result.Leaks));
            var text = result.Text;
            foreach (var original in new[] { "FAKESURNAME", "FAKEGIVEN", "123-4-56789", "P. O. Box TEST 999", "19700315", "4711" })
                Assert.IsFalse(text.Contains(original), "Still contains " + original);

            Assert.AreEqual("19700101", Element(text, "DMG", 2));
            Assert.AreEqual("REDACTED", Element(text, "NTE", 2));
        }

        [TestMethod]
        public void KeepsStructureCodesAmountsProvidersAndPayer()
        {
            var result = Create().Anonymize(Claim837);
            var text = result.Text;

            Assert.AreEqual(Claim837.Split('~').Length, text.Split('~').Length);
            Assert.IsTrue(text.StartsWith(Claim837.Substring(0, 106), StringComparison.Ordinal), "ISA must not change");
            Assert.IsTrue(text.Contains("SE*26*0080~"));
            Assert.IsTrue(text.Contains("HI*ABK:E119*ABF:E785~"));
            Assert.IsTrue(text.Contains("SV1*HC:97803*95*UN*1***1:2~"));
            Assert.IsTrue(text.Contains("NM1*82*1*Doctorson*Dana*A***XX*1234567893~"), "Providers are kept");
            Assert.IsTrue(text.Contains("NM1*PR*2*Synthetic Insurance Co.*****PI*12345~"), "Payer is kept");
            Assert.IsTrue(text.Contains("N4*Paget*BM*00000*BE~"), "City, state and country are kept");
            Assert.AreEqual("837", result.TransactionType);
            Assert.AreEqual("Synthetic Insurance Co.", result.PayerName);
        }

        [TestMethod]
        public void PseudonymsKeepFormat()
        {
            var text = Create().Anonymize(Claim837).Text;

            var memberId = Element(text, "NM1*IL", 9);
            Assert.AreEqual(11, memberId.Length);
            Assert.AreEqual('-', memberId[3]);
            Assert.AreEqual('-', memberId[5]);
            Assert.IsTrue(memberId.Replace("-", "").All(char.IsDigit), memberId);

            var lastName = Element(text, "NM1*IL", 3);
            Assert.AreEqual("FAKESURNAME".Length, lastName.Length);
            Assert.IsTrue(lastName.All(char.IsUpper), lastName);

            Assert.AreEqual("123-4".Length, Element(text, "SBR", 3).Length);
        }

        [TestMethod]
        public void SameKeyGivesSamePseudonymsAcrossFiles()
        {
            var anonymizer = Create();
            var claim = anonymizer.Anonymize(Claim837).Text;
            var remit = anonymizer.Anonymize(Remit835).Text;

            Assert.AreEqual(claim, Create().Anonymize(Claim837).Text, "Deterministic for the same key");

            // 837 CLM01 still matches 835 CLP01, line control numbers and member IDs still match.
            Assert.AreEqual(Element(claim, "CLM", 1), Element(remit, "CLP", 1));
            Assert.AreEqual(Element(claim, "REF*6R", 2), Element(remit, "REF*6R", 2));
            Assert.AreEqual(Element(claim, "NM1*IL", 9), Element(remit, "NM1*QC", 9));
            Assert.AreEqual(Element(claim, "NM1*IL", 3), Element(remit, "NM1*QC", 3));
        }

        [TestMethod]
        public void DifferentKeyGivesDifferentPseudonyms()
        {
            var a = Create("first-key-0123456789").Anonymize(Claim837).Text;
            var b = Create("other-key-0123456789").Anonymize(Claim837).Text;
            Assert.AreNotEqual(Element(a, "NM1*IL", 3), Element(b, "NM1*IL", 3));
        }

        [TestMethod]
        public void DatesShiftTogether()
        {
            var anonymizer = Create();
            Assert.IsTrue(anonymizer.DateShiftDays <= -30 && anonymizer.DateShiftDays >= -329);

            var range = Element(anonymizer.Anonymize(Claim837).Text, "DTP*472", 3).Split('-');
            var from = DateTime.ParseExact(range[0], "yyyyMMdd", null);
            var to = DateTime.ParseExact(range[1], "yyyyMMdd", null);
            Assert.AreEqual(new DateTime(2024, 4, 10).AddDays(anonymizer.DateShiftDays), from);
            Assert.AreEqual(2, (to - from).Days, "Intervals are preserved");
        }

        [TestMethod]
        public void AnonymizedRemittanceStillReadsAndBalances()
        {
            var text = Create().Anonymize(Remit835).Text;
            var read = Remittance835Reader.Read(text);

            Assert.AreEqual(0, read.Issues.Count, string.Join("; ", read.Issues));
            var advice = read.Transactions.Single();
            Assert.AreEqual(0, RemittanceBalancing.Check(advice).Count);
            Assert.AreNotEqual("PAYERCLAIM99", advice.Claims[0].PayerClaimControlNumber);
            Assert.AreEqual(80m, advice.Claims[0].PaymentAmount);
        }

        [TestMethod]
        public void UnknownSegmentWithPatientDataBlocksOutput()
        {
            // A segment the anonymizer does not know carries the patient's name: output must be withheld.
            var withLeak = Claim837.Replace("LX*1~", "LX*1~ZZZ*FAKESURNAME~").Replace("SE*26*", "SE*27*");
            var result = Create().Anonymize(withLeak);

            Assert.IsNull(result.Text);
            CollectionAssertContains(result.Leaks.ToArray(), "ZZZ");
        }

        [TestMethod]
        public void KeepsLineBreaksBetweenSegments()
        {
            var withBreaks = Claim837.Replace("~", "~\r\n");
            var text = Create().Anonymize(withBreaks).Text;
            Assert.AreEqual(withBreaks.Split("\r\n").Length, text.Split("\r\n").Length);
            Assert.IsFalse(text.Contains("FAKESURNAME"));
        }

        [TestMethod]
        public void RejectsShortKey()
        {
            try
            {
                new X12Anonymizer(Encoding.UTF8.GetBytes("short"));
                Assert.Fail("Expected ArgumentException");
            }
            catch (ArgumentException)
            {
            }
        }

        private static void CollectionAssertContains(string[] values, string expected)
        {
            Assert.IsTrue(values.Contains(expected), string.Join(",", values));
        }
    }
}
