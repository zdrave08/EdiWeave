// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.
// All data below is synthetic. Never put real patient data in tests.

using System;
using EdiWeave.Healthcare;

namespace EdiWeave.UnitTests.Healthcare
{
    internal static class TestData
    {
        public const string BillingNpi = "1234567893";
        public const string RenderingNpi = "1245319599";

        public static X12Options Options(int icn = 1, int gcn = 1) => new X12Options
        {
            SenderId = "SYNCLINIC",
            ReceiverId = "SYNHOUSE",
            Production = false,
            InterchangeControlNumber = icn,
            GroupControlNumber = gcn,
            Timestamp = new DateTime(2026, 11, 17, 10, 30, 0),
            NewLineAfterSegment = false
        };

        public static Claim837PBatch Batch()
        {
            var batch = new Claim837PBatch
            {
                Submitter = new Organization { Name = "SYNTHETIC CLINIC", Id = "SYNCLINIC", ContactName = "BILLING OFFICE", ContactPhone = "441-555-0100" },
                Receiver = new Organization { Name = "SYNTHETIC CLEARINGHOUSE", Id = "SYNHOUSE" },
                Billing = new BillingProvider
                {
                    Name = "SYNTHETIC CLINIC",
                    Npi = BillingNpi,
                    Tin = "12-3456789",
                    TaxonomyCode = "207Q00000X",
                    Address = new Address { Line1 = "2 SAMPLE LANE", City = "PEMBROKE", State = "HM", PostalCode = "12345" }
                }
            };
            batch.Claims.Add(Claim("CLAIM-001", "TESTPATIENT", "ALPHA", "SYN0001"));
            return batch;
        }

        public static Claim837P Claim(string pcn, string last, string first, string member)
        {
            var claim = new Claim837P
            {
                PatientControlNumber = pcn,
                TotalCharge = 200m,
                Payer = new Payer { Name = "SYNTHETIC HEALTH INSURER", Id = "PAYER01" },
                Subscriber = new Subscriber
                {
                    LastName = last,
                    FirstName = first,
                    MemberId = member,
                    GroupNumber = "GRP100",
                    DateOfBirth = new DateTime(1980, 5, 17),
                    Gender = "F",
                    Address = new Address { Line1 = "1 EXAMPLE STREET", City = "HAMILTON", State = "HM", PostalCode = "123456789" }
                }
            };
            claim.Diagnoses.Add("J069");
            claim.Diagnoses.Add("R5081");
            claim.ServiceLines.Add(Line("99214", 120m, pcn + "-1", new DateTime(2026, 11, 3), "25"));
            claim.ServiceLines.Add(Line("93000", 80m, pcn + "-2", new DateTime(2026, 11, 3)));
            return claim;
        }

        public static ServiceLine Line(string code, decimal charge, string control, DateTime date, string modifier = null)
        {
            var line = new ServiceLine { ProcedureCode = code, Charge = charge, ServiceDate = date, LineControlNumber = control };
            if (modifier != null) line.Modifiers.Add(modifier);
            return line;
        }

        public const string Ack999Accepted =
            "ISA*00*          *00*          *ZZ*SYNHOUSE       *ZZ*SYNCLINIC      *261117*1100*^*00501*000000771*0*T*:~" +
            "GS*FA*SYNHOUSE*SYNCLINIC*20261117*1100*5*X*005010X231A1~ST*999*0001*005010X231A1~" +
            "AK1*HC*1*005010X222A1~AK2*837*0001*005010X222A1~IK5*A~AK9*A*1*1*1~SE*6*0001~GE*1*5~IEA*1*000000771~";

        public const string Ack999Rejected =
            "ISA*00*          *00*          *ZZ*SYNHOUSE       *ZZ*SYNCLINIC      *261117*1100*^*00501*000000772*0*T*:~" +
            "GS*FA*SYNHOUSE*SYNCLINIC*20261117*1100*6*X*005010X231A1~ST*999*0001*005010X231A1~" +
            "AK1*HC*2*005010X222A1~AK2*837*0001*005010X222A1~IK3*NM1*7*2010BA*8~IK4*3*1036*7*BAD~IK5*R*5~AK9*R*1*1*0~SE*9*0001~GE*1*6~IEA*1*000000772~";

        public const string Ta1Rejected =
            "ISA*00*          *00*          *ZZ*SYNHOUSE       *ZZ*SYNCLINIC      *261117*1100*^*00501*000000773*0*T*:~" +
            "TA1*000000003*261117*1030*R*023~IEA*0*000000773~";

        public const string Claim277 =
            "ISA*00*          *00*          *ZZ*SYNPAYER       *ZZ*SYNCLINIC      *261117*1200*^*00501*000000881*0*T*:~" +
            "GS*HN*SYNPAYER*SYNCLINIC*20261117*1200*7*X*005010X214~ST*277*0001*005010X214~BHT*0085*08*REF1*20261117*1200*TH~" +
            "HL*1**20*1~NM1*PR*2*SYNTHETIC HEALTH INSURER*****PI*PAYER01~" +
            "HL*2*1*21*1~NM1*41*2*SYNTHETIC CLINIC*****46*SYNCLINIC~" +
            "HL*3*2*19*1~NM1*85*2*SYNTHETIC CLINIC*****XX*1234567893~" +
            "HL*4*3*PT*0~NM1*QC*1*TESTPATIENT*ALPHA~TRN*2*CLAIM-001~STC*A2:20*20261117*WQ*200~REF*1K*PAYERCTRL01~REF*D9*CH0001~" +
            "HL*5*3*PT*0~NM1*QC*1*TESTPATIENT*BETA~TRN*2*CLAIM-002~STC*A3:187:QC*20261117*U*95~STC*A7:33:85*20261117*U*95**********DOB MISSING~" +
            "SE*17*0001~GE*1*7~IEA*1*000000881~";

        public const string Eligibility271 =
            "ISA*00*          *00*          *ZZ*SYNPAYER       *ZZ*SYNCLINIC      *261117*1200*^*00501*000000991*0*T*:~" +
            "GS*HB*SYNPAYER*SYNCLINIC*20261117*1200*9*X*005010X279A1~ST*271*0001*005010X279A1~BHT*0022*11*TRACE77*20261117*1200~" +
            "HL*1**20*1~NM1*PR*2*SYNTHETIC HEALTH INSURER*****PI*PAYER01~" +
            "HL*2*1*21*1~NM1*1P*2*SYNTHETIC CLINIC*****XX*1234567893~" +
            "HL*3*2*22*0~TRN*2*TRACE77*9SYNCLINIC~NM1*IL*1*TESTPATIENT*ALPHA****MI*SYN0001~" +
            "EB*1*IND*30^98**GOLD PLAN~EB*B*IND*98***27*25~EB*C*IND*30***23*500~EB*A*IND*98***27**0.2~EB*G*IND*30***23*3000~" +
            "SE*13*0001~GE*1*9~IEA*1*000000991~";

        public const string Eligibility271Rejected =
            "ISA*00*          *00*          *ZZ*SYNPAYER       *ZZ*SYNCLINIC      *261117*1200*^*00501*000000992*0*T*:~" +
            "GS*HB*SYNPAYER*SYNCLINIC*20261117*1200*10*X*005010X279A1~ST*271*0001*005010X279A1~BHT*0022*11*TRACE78*20261117*1200~" +
            "HL*1**20*1~NM1*PR*2*SYNTHETIC HEALTH INSURER*****PI*PAYER01~" +
            "HL*2*1*21*1~NM1*1P*2*SYNTHETIC CLINIC*****XX*1234567893~" +
            "HL*3*2*22*0~TRN*2*TRACE78*9SYNCLINIC~NM1*IL*1*TESTPATIENT*ALPHA****MI*BAD~AAA*N**75*C~" +
            "SE*10*0001~GE*1*10~IEA*1*000000992~";

        // Payment advice for CLAIM-001 (paid in part, lines matched by REF*6R) and CLAIM-002 (denied), plus one claim nobody sent.
        public const string Remittance835 =
            "ISA*00*          *00*          *ZZ*SYNPAYER       *ZZ*SYNCLINIC      *261120*1200*^*00501*000000301*0*T*:~" +
            "GS*HP*SYNPAYER*SYNCLINIC*20261120*1200*1*X*005010X221A1~ST*835*0001*005010X221A1~" +
            "BPR*I*150*C*ACH*CCP*01*999999999*DA*123456*1999999999**01*888888888*DA*98765*20261120~TRN*1*EFT0000777*1999999999~" +
            "N1*PR*SYNTHETIC HEALTH INSURER~N1*PE*SYNTHETIC CLINIC*XX*1234567893~" +
            "LX*1~CLP*CLAIM-001*1*200*130*20*12*PAYERCTRL01*11*1~" +
            "SVC*HC>99214>25*120*90**1~CAS*CO*45*20~CAS*PR*3*10~REF*6R*CLAIM-001-1~" +
            "SVC*HC>93000*80*40**1~CAS*CO*45*30~CAS*PR*2*10~REF*6R*CLAIM-001-2~" +
            "CLP*CLAIM-002*4*95*0**12*PAYERCTRL02*11*1~CAS*CO*50*95~" +
            "CLP*CLAIM-999*1*50*50**12*PAYERCTRL03*11*1~" +
            "SE*17*0001~GE*1*1~IEA*1*000000301~";
    }
}
