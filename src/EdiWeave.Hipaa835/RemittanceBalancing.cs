// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EdiWeave.Hipaa835
{
    /// <summary>
    /// Balancing checks from the 835 implementation guide. An 835 that does not balance should not be
    /// posted automatically.
    /// </summary>
    public static class RemittanceBalancing
    {
        /// <summary>
        /// Checks that:
        /// <list type="bullet">
        /// <item>each service line: SVC02 charge − service adjustments = SVC03 paid;</item>
        /// <item>each claim: CLP03 charge − (claim + service adjustments) = CLP04 paid;</item>
        /// <item>the payment: Σ CLP04 − Σ PLB amounts = BPR02.</item>
        /// </list>
        /// </summary>
        public static List<Remittance835Issue> Check(RemittanceAdvice advice)
        {
            var issues = new List<Remittance835Issue>();
            if (advice == null) return issues;

            foreach (var claim in advice.Claims)
            {
                var serviceAdjustments = 0m;
                for (var i = 0; i < claim.ServiceLines.Count; i++)
                {
                    var line = claim.ServiceLines[i];
                    var lineAdjustments = line.Adjustments.Sum(a => a.Amount);
                    serviceAdjustments += lineAdjustments;
                    if (line.Charge - lineAdjustments != line.Paid)
                        issues.Add(Error(advice, claim.SegmentIndex,
                            $"Claim {claim.PatientControlNumber}, service line {i + 1} ({line.ProcedureCode}): " +
                            $"charge {F(line.Charge)} − adjustments {F(lineAdjustments)} ≠ paid {F(line.Paid)}."));
                }

                var claimAdjustments = claim.Adjustments.Sum(a => a.Amount);
                var totalAdjustments = claimAdjustments + serviceAdjustments;
                if (claim.TotalCharge - totalAdjustments != claim.PaymentAmount)
                    issues.Add(Error(advice, claim.SegmentIndex,
                        $"Claim {claim.PatientControlNumber}: charge {F(claim.TotalCharge)} − adjustments " +
                        $"{F(totalAdjustments)} ≠ paid {F(claim.PaymentAmount)}."));
            }

            var claimsPaid = advice.Claims.Sum(c => c.PaymentAmount);
            var providerAdjustments = advice.ProviderAdjustments.Sum(p => p.Amount);
            if (claimsPaid - providerAdjustments != advice.Payment.TotalPayment)
                issues.Add(Error(advice, null,
                    $"Payment does not balance: claims paid {F(claimsPaid)} − provider adjustments " +
                    $"{F(providerAdjustments)} ≠ BPR02 {F(advice.Payment.TotalPayment)}."));

            return issues;
        }

        private static Remittance835Issue Error(RemittanceAdvice advice, int? index, string message) =>
            new Remittance835Issue(IssueSeverity.Error, message, advice.ControlNumber, index);

        private static string F(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
