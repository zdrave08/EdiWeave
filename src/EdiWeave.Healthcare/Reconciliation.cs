// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Linq;
using EdiWeave.Hipaa835;

namespace EdiWeave.Healthcare
{
    /// <summary>Where a submitted claim stands, taking every response received so far into account.</summary>
    public enum ClaimOutcome
    {
        /// <summary>Nothing has been received for the claim yet.</summary>
        Submitted,

        /// <summary>A 277CA says the claim was accepted, and no payment advice exists yet.</summary>
        Accepted,

        /// <summary>A 277CA says the claim is pending, and no payment advice exists yet.</summary>
        Pending,

        /// <summary>A 277CA rejected the claim. It was never adjudicated: correct it and resubmit.</summary>
        Rejected,

        /// <summary>The 835 paid something (CLP02 = 1, 2, 3, 19, 20 or 21 with a payment above zero).</summary>
        Paid,

        /// <summary>The 835 processed the claim and paid nothing, or denied it (CLP02 = 4).</summary>
        Denied,

        /// <summary>The 835 reverses an earlier payment (CLP02 = 22).</summary>
        Reversed
    }

    /// <summary>Billed against paid for one service line.</summary>
    public sealed class LineReconciliation
    {
        /// <summary>Your line control number, when the line was matched by it.</summary>
        public string LineControlNumber { get; internal set; }

        /// <summary>Procedure code billed.</summary>
        public string ProcedureCode { get; internal set; }

        /// <summary>Amount billed on the 837.</summary>
        public decimal Billed { get; internal set; }

        /// <summary>Amount the payer paid on this line.</summary>
        public decimal Paid { get; internal set; }

        /// <summary>Sum of all adjustments on the line (contractual, patient responsibility, other).</summary>
        public decimal Adjusted { get; internal set; }

        /// <summary>True when the line was found in the 835.</summary>
        public bool MatchedInRemittance { get; internal set; }
    }

    /// <summary>The result for one submitted claim.</summary>
    public sealed class ClaimReconciliation
    {
        /// <summary>Your patient control number.</summary>
        public string PatientControlNumber { get; internal set; }

        /// <summary>Where the claim stands.</summary>
        public ClaimOutcome Outcome { get; internal set; }

        /// <summary>Total billed on the 837.</summary>
        public decimal Billed { get; internal set; }

        /// <summary>Total paid according to the 835 (zero when there is no remittance).</summary>
        public decimal Paid { get; internal set; }

        /// <summary>Patient responsibility (CLP05) from the 835.</summary>
        public decimal PatientResponsibility { get; internal set; }

        /// <summary>Sum of all claim and line adjustments from the 835.</summary>
        public decimal Adjustments { get; internal set; }

        /// <summary>Payer claim control number from the 835 or the 277CA.</summary>
        public string PayerClaimControlNumber { get; internal set; }

        /// <summary>The payment advice the claim was matched to, or null.</summary>
        public ClaimPayment Remittance { get; internal set; }

        /// <summary>The 277CA entry the claim was matched to, or null.</summary>
        public ClaimStatus Status { get; internal set; }

        /// <summary>Line by line comparison.</summary>
        public List<LineReconciliation> Lines { get; } = new List<LineReconciliation>();

        /// <summary>Things worth a look: amounts that do not add up, lines the payer did not return, and so on.</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>The result of <see cref="ClaimReconciler.Reconcile"/>.</summary>
    public sealed class ReconciliationResult
    {
        /// <summary>One entry per submitted claim, in the order given.</summary>
        public List<ClaimReconciliation> Claims { get; } = new List<ClaimReconciliation>();

        /// <summary>Claims in the 835 that match no submitted claim (payments for something you did not send, or sent elsewhere).</summary>
        public List<ClaimPayment> UnmatchedPayments { get; } = new List<ClaimPayment>();

        /// <summary>Total billed on the claims given.</summary>
        public decimal TotalBilled => Claims.Sum(c => c.Billed);

        /// <summary>Total paid on matched claims.</summary>
        public decimal TotalPaid => Claims.Sum(c => c.Paid);
    }

    /// <summary>
    /// Matches the claims you sent (837P) with the answers that come back: 277CA statuses and 835 payments.
    /// Claims match on the patient control number (CLM01 = TRN02 = CLP01). Lines match on REF*6R, and fall back
    /// to procedure code and position when the payer does not echo it.
    /// </summary>
    public static class ClaimReconciler
    {
        /// <summary>Tolerance when comparing amounts.</summary>
        private const decimal Tolerance = 0.01m;

        /// <summary>Reconciles submitted claims with payment advice and optional 277CA statuses.</summary>
        public static ReconciliationResult Reconcile(IEnumerable<Claim837P> submitted,
            IEnumerable<RemittanceAdvice> remittances, IEnumerable<ClaimStatus> statuses = null)
        {
            if (submitted == null) throw new ArgumentNullException(nameof(submitted));
            var result = new ReconciliationResult();

            var payments = (remittances ?? Enumerable.Empty<RemittanceAdvice>())
                .SelectMany(r => r.Claims).ToList();
            var byPcn = new Dictionary<string, List<ClaimPayment>>();
            foreach (var p in payments)
            {
                if (string.IsNullOrEmpty(p.PatientControlNumber)) continue;
                if (!byPcn.TryGetValue(p.PatientControlNumber, out var list)) byPcn[p.PatientControlNumber] = list = new List<ClaimPayment>();
                list.Add(p);
            }
            var statusByPcn = (statuses ?? Enumerable.Empty<ClaimStatus>())
                .Where(s => !string.IsNullOrEmpty(s.PatientControlNumber))
                .GroupBy(s => s.PatientControlNumber)
                .ToDictionary(g => g.Key, g => g.Last());

            var matched = new HashSet<ClaimPayment>();

            foreach (var claim in submitted)
            {
                var rec = new ClaimReconciliation
                {
                    PatientControlNumber = claim.PatientControlNumber,
                    Billed = claim.TotalCharge,
                    Outcome = ClaimOutcome.Submitted
                };
                statusByPcn.TryGetValue(claim.PatientControlNumber ?? string.Empty, out var status);
                rec.Status = status;
                rec.PayerClaimControlNumber = status?.PayerClaimControlNumber;

                if (status != null)
                {
                    switch (status.Outcome)
                    {
                        case ClaimStatusOutcome.Rejected: rec.Outcome = ClaimOutcome.Rejected; break;
                        case ClaimStatusOutcome.Accepted: rec.Outcome = ClaimOutcome.Accepted; break;
                        case ClaimStatusOutcome.Pending: rec.Outcome = ClaimOutcome.Pending; break;
                    }
                }

                List<ClaimPayment> found = null;
                if (claim.PatientControlNumber != null) byPcn.TryGetValue(claim.PatientControlNumber, out found);

                if (found != null && found.Count > 0)
                {
                    // The last payment advice wins, except that a reversal followed by a new payment ends as paid.
                    foreach (var f in found) matched.Add(f);
                    var latest = found[found.Count - 1];
                    rec.Remittance = latest;
                    rec.PayerClaimControlNumber = latest.PayerClaimControlNumber ?? rec.PayerClaimControlNumber;
                    rec.Paid = found.Sum(f => f.StatusCode == "22" ? -f.PaymentAmount : f.PaymentAmount);
                    rec.PatientResponsibility = latest.PatientResponsibility ?? 0m;

                    var claimAdj = latest.Adjustments.Sum(a => a.Amount);
                    var lineAdj = latest.ServiceLines.Sum(l => l.Adjustments.Sum(a => a.Amount));
                    rec.Adjustments = claimAdj + lineAdj;

                    rec.Outcome = Outcome(latest, rec.Paid);
                    Compare(claim, latest, rec);
                }
                else if (rec.Outcome == ClaimOutcome.Submitted && status == null)
                {
                    rec.Notes.Add("No 277CA status and no payment advice received yet.");
                }

                result.Claims.Add(rec);
            }

            foreach (var p in payments)
                if (!matched.Contains(p)) result.UnmatchedPayments.Add(p);

            return result;
        }

        private static ClaimOutcome Outcome(ClaimPayment payment, decimal netPaid)
        {
            if (payment.StatusCode == "22") return netPaid > 0 ? ClaimOutcome.Paid : ClaimOutcome.Reversed;
            if (payment.StatusCode == "4") return ClaimOutcome.Denied;
            return netPaid > 0 ? ClaimOutcome.Paid : ClaimOutcome.Denied;
        }

        private static void Compare(Claim837P claim, ClaimPayment payment, ClaimReconciliation rec)
        {
            if (Math.Abs(payment.TotalCharge - claim.TotalCharge) > Tolerance)
                rec.Notes.Add("The payer lists a different total charge than the claim sent.");

            // Line matching: by line control number first, then by procedure code in order.
            var remaining = new List<ServicePayment>(payment.ServiceLines);
            foreach (var line in claim.ServiceLines)
            {
                ServicePayment pay = null;
                if (!string.IsNullOrEmpty(line.LineControlNumber))
                    pay = remaining.FirstOrDefault(r => r.LineItemControlNumber == line.LineControlNumber);
                if (pay == null)
                    pay = remaining.FirstOrDefault(r => string.IsNullOrEmpty(r.LineItemControlNumber) &&
                                                         r.ProcedureCode == line.ProcedureCode);
                var lr = new LineReconciliation
                {
                    LineControlNumber = line.LineControlNumber,
                    ProcedureCode = line.ProcedureCode,
                    Billed = line.Charge,
                    MatchedInRemittance = pay != null
                };
                if (pay != null)
                {
                    remaining.Remove(pay);
                    lr.Paid = pay.Paid;
                    lr.Adjusted = pay.Adjustments.Sum(a => a.Amount);
                    if (Math.Abs(pay.Charge - line.Charge) > Tolerance)
                        rec.Notes.Add("The payer lists a different charge for a line (" + line.ProcedureCode + ").");
                }
                rec.Lines.Add(lr);
            }

            if (payment.ServiceLines.Count > 0)
            {
                foreach (var l in rec.Lines.Where(l => !l.MatchedInRemittance))
                    rec.Notes.Add("Line " + l.ProcedureCode + " was not returned in the payment advice.");
                if (remaining.Count > 0)
                    rec.Notes.Add(remaining.Count + " payment line(s) in the advice match no line you billed.");
            }

            // Everything billed should be explained by payment plus adjustments.
            var accounted = rec.Paid + rec.Adjustments;
            if (payment.StatusCode != "22" && Math.Abs(claim.TotalCharge - accounted) > Tolerance)
                rec.Notes.Add("Billed " + claim.TotalCharge.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                              " but payment and adjustments account for " +
                              accounted.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ".");

            if (payment.StatusCode == "4" && rec.Paid > 0)
                rec.Notes.Add("The claim is marked denied but a payment is listed.");
        }
    }
}
