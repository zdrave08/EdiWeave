// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace EdiWeave.Hipaa835
{
    /// <summary>
    /// Reads HIPAA 5010 835 (005010X221A1) files into <see cref="RemittanceAdvice"/> objects.
    /// The reader never throws on bad content: problems are reported in <see cref="Remittance835Result.Issues"/>.
    /// Amounts and dates are parsed with the invariant culture.
    /// </summary>
    public static class Remittance835Reader
    {
        private enum Section
        {
            Header,
            Payer,
            Payee,
            Claim,
            Service,
            Trailer
        }

        /// <summary>Reads an 835 from a stream. The default encoding is UTF-8 (with BOM detection).</summary>
        public static Remittance835Result Read(Stream stream, Encoding encoding = null)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using (var reader = new StreamReader(stream, encoding ?? new UTF8Encoding(false), true))
            {
                return Read(reader.ReadToEnd());
            }
        }

        /// <summary>Reads an 835 from text.</summary>
        public static Remittance835Result Read(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            var result = new Remittance835Result();
            IReadOnlyList<X12Segment> segments;
            try
            {
                segments = X12Tokenizer.Tokenize(text, out var delimiters);
                result.Delimiters = delimiters;
            }
            catch (FormatException ex)
            {
                result.Issues.Add(new Remittance835Issue(IssueSeverity.Error, ex.Message));
                return result;
            }

            new State(result).Run(segments);
            return result;
        }

        private sealed class State
        {
            private readonly Remittance835Result _result;
            private RemittanceAdvice _current;
            private ClaimPayment _claim;
            private ServicePayment _service;
            private Section _section;
            private int _stIndex;
            private string _gsSender;
            private string _gsReceiver;
            private int _groupTransactions;
            private int _interchangeGroups;
            private bool _sawIea;

            public State(Remittance835Result result)
            {
                _result = result;
            }

            public void Run(IReadOnlyList<X12Segment> segments)
            {
                foreach (var s in segments)
                {
                    try
                    {
                        Handle(s);
                    }
                    catch (FormatException ex)
                    {
                        Issue(IssueSeverity.Error, $"{s.Id}: {ex.Message}", s.Index);
                    }
                    catch (Exception ex)
                    {
                        // The reader must not throw on bad content; report and continue.
                        Issue(IssueSeverity.Error, $"{s.Id}: unexpected {ex.GetType().Name}: {ex.Message}", s.Index);
                    }
                }

                if (_current != null)
                    Issue(IssueSeverity.Error, "Transaction set has no SE trailer.", null);
                if (!_sawIea)
                    _result.Issues.Add(new Remittance835Issue(IssueSeverity.Error, "Interchange has no IEA trailer."));
            }

            private void Handle(X12Segment s)
            {
                switch (s.Id)
                {
                    case "ISA":
                        _result.InterchangeSenderId = s[6].Trim();
                        _result.InterchangeReceiverId = s[8].Trim();
                        _result.InterchangeControlNumber = s[13].Trim();
                        _interchangeGroups = 0;
                        return;
                    case "GS":
                        _gsSender = s[2];
                        _gsReceiver = s[3];
                        _groupTransactions = 0;
                        _interchangeGroups++;
                        return;
                    case "ST":
                        StartTransaction(s);
                        return;
                    case "SE":
                        EndTransaction(s);
                        return;
                    case "GE":
                        CheckCount("GE01", s[1], _groupTransactions, s.Index);
                        return;
                    case "IEA":
                        _sawIea = true;
                        CheckCount("IEA01", s[1], _interchangeGroups, s.Index);
                        if (s[2].Trim() != _result.InterchangeControlNumber)
                            _result.Issues.Add(new Remittance835Issue(IssueSeverity.Error,
                                "IEA02 does not match ISA13.", null, s.Index));
                        return;
                }

                if (_current == null)
                {
                    _result.Issues.Add(new Remittance835Issue(IssueSeverity.Warning,
                        $"Segment {s.Id} outside a transaction set was ignored.", null, s.Index));
                    return;
                }

                HandleTransactionSegment(s);
            }

            private void StartTransaction(X12Segment s)
            {
                if (_current != null)
                    Issue(IssueSeverity.Error, "ST found before the previous transaction's SE.", s.Index);

                _current = new RemittanceAdvice
                {
                    ControlNumber = s[2],
                    ImplementationReference = s[3],
                    GroupSenderCode = _gsSender,
                    GroupReceiverCode = _gsReceiver
                };
                _stIndex = s.Index;
                _claim = null;
                _service = null;
                _section = Section.Header;
                _groupTransactions++;

                if (s[1] != "835")
                    Issue(IssueSeverity.Error, $"Transaction set {s[1]} is not an 835.", s.Index);
                if (!string.IsNullOrEmpty(s[3]) && s[3] != "005010X221A1")
                    Issue(IssueSeverity.Warning, $"Implementation reference {s[3]} is not 005010X221A1.", s.Index);

                _result.Transactions.Add(_current);
            }

            private void EndTransaction(X12Segment s)
            {
                if (_current == null)
                {
                    _result.Issues.Add(new Remittance835Issue(IssueSeverity.Error, "SE without ST.", null, s.Index));
                    return;
                }

                _current.ActualSegmentCount = s.Index - _stIndex + 1;
                if (int.TryParse(s[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared))
                {
                    _current.DeclaredSegmentCount = declared;
                    if (declared != _current.ActualSegmentCount)
                        Issue(IssueSeverity.Error,
                            $"SE01 says {declared} segments, the transaction has {_current.ActualSegmentCount}.", s.Index);
                }
                else
                {
                    Issue(IssueSeverity.Error, "SE01 is not a number.", s.Index);
                }

                if (s[2] != _current.ControlNumber)
                    Issue(IssueSeverity.Error, "SE02 does not match ST02.", s.Index);

                _current = null;
                _claim = null;
                _service = null;
            }

            private void HandleTransactionSegment(X12Segment s)
            {
                switch (s.Id)
                {
                    case "BPR":
                        var p = _current.Payment;
                        p.TransactionHandlingCode = s[1];
                        p.TotalPayment = Amount(s[2]);
                        p.CreditDebitFlag = s[3];
                        p.PaymentMethod = s[4];
                        p.PaymentFormat = s[5];
                        p.PaymentDate = OptionalDate(s[16]);
                        return;
                    case "TRN":
                        _current.TraceNumber = s[2];
                        _current.TraceOriginatorId = s[3];
                        return;
                    case "N1":
                        StartParty(s);
                        return;
                    case "N3":
                        CurrentParty(s)?.AddressLines.AddRange(NonEmpty(s[1], s[2]));
                        return;
                    case "N4":
                        var party = CurrentParty(s);
                        if (party == null) return;
                        party.City = s[1];
                        party.State = s[2];
                        party.PostalCode = s[3];
                        party.CountryCode = s[4];
                        return;
                    case "LX":
                    case "TS3":
                    case "TS2":
                        // Header number and provider summary: not needed for posting.
                        _section = Section.Claim;
                        _current.UnmappedSegments.Add(s);
                        return;
                    case "CLP":
                        StartClaim(s);
                        return;
                    case "SVC":
                        StartService(s);
                        return;
                    case "CAS":
                        AddAdjustments(s);
                        return;
                    case "NM1":
                        AddName(s);
                        return;
                    case "DTM":
                        AddDate(s);
                        return;
                    case "REF":
                        AddReference(s);
                        return;
                    case "AMT":
                        AddAmount(s);
                        return;
                    case "LQ":
                        if (_section == Section.Service && _service != null)
                            _service.RemarkCodes.Add(new KeyValuePair<string, string>(s[1], s[2]));
                        else
                            _current.UnmappedSegments.Add(s);
                        return;
                    case "PLB":
                        AddProviderAdjustments(s);
                        return;
                    default:
                        _current.UnmappedSegments.Add(s);
                        return;
                }
            }

            private void StartParty(X12Segment s)
            {
                Party party;
                switch (s[1])
                {
                    case "PR":
                        _section = Section.Payer;
                        party = _current.Payer;
                        break;
                    case "PE":
                        _section = Section.Payee;
                        party = _current.Payee;
                        break;
                    default:
                        _current.UnmappedSegments.Add(s);
                        return;
                }

                party.Name = s[2];
                party.IdQualifier = s[3];
                party.Id = s[4];
            }

            private Party CurrentParty(X12Segment s)
            {
                switch (_section)
                {
                    case Section.Payer:
                        return _current.Payer;
                    case Section.Payee:
                        return _current.Payee;
                    default:
                        _current.UnmappedSegments.Add(s);
                        return null;
                }
            }

            private void StartClaim(X12Segment s)
            {
                // Reset first: if CLP cannot be parsed, its segments must not attach to the previous claim.
                _section = Section.Claim;
                _service = null;
                _claim = null;
                var claim = new ClaimPayment
                {
                    PatientControlNumber = s[1],
                    StatusCode = s[2],
                    TotalCharge = Amount(s[3]),
                    PaymentAmount = Amount(s[4]),
                    PatientResponsibility = OptionalAmount(s[5]),
                    FilingIndicator = s[6],
                    PayerClaimControlNumber = s[7],
                    FacilityCode = s[8],
                    FrequencyCode = s[9],
                    SegmentIndex = s.Index
                };
                _claim = claim;
                _current.Claims.Add(claim);
            }

            private void StartService(X12Segment s)
            {
                if (_claim == null)
                {
                    Issue(IssueSeverity.Error, "SVC without a preceding CLP.", s.Index);
                    return;
                }

                // Reset first: if SVC cannot be parsed, its segments must not attach to the previous line.
                _section = Section.Service;
                _service = null;
                var procedure = X12Tokenizer.SplitComponents(s[1], _result.Delimiters);
                var service = new ServicePayment
                {
                    ProcedureQualifier = procedure.Length > 0 ? procedure[0] : string.Empty,
                    ProcedureCode = procedure.Length > 1 ? procedure[1] : string.Empty,
                    Charge = Amount(s[2]),
                    Paid = Amount(s[3]),
                    RevenueCode = s[4],
                    UnitsPaid = OptionalAmount(s[5]) ?? 1m,
                    OriginalProcedure = s[6]
                };
                for (var i = 2; i < procedure.Length && i < 6; i++)
                {
                    if (!string.IsNullOrEmpty(procedure[i])) service.Modifiers.Add(procedure[i]);
                }

                _service = service;
                _claim.ServiceLines.Add(service);
            }

            private void AddAdjustments(X12Segment s)
            {
                List<Adjustment> target;
                if (_section == Section.Service && _service != null) target = _service.Adjustments;
                else if (_section == Section.Claim && _claim != null) target = _claim.Adjustments;
                else
                {
                    Issue(IssueSeverity.Warning, "CAS outside a claim was ignored.", s.Index);
                    return;
                }

                // CAS01 group code, then up to six reason/amount/quantity triplets (CAS02..CAS19).
                for (var i = 2; i <= 17; i += 3)
                {
                    if (string.IsNullOrEmpty(s[i])) continue;
                    target.Add(new Adjustment
                    {
                        GroupCode = s[1],
                        ReasonCode = s[i],
                        Amount = Amount(s[i + 1]),
                        Quantity = OptionalAmount(s[i + 2])
                    });
                }
            }

            private void AddName(X12Segment s)
            {
                if ((_section != Section.Claim && _section != Section.Service) || _claim == null)
                {
                    _current.UnmappedSegments.Add(s);
                    return;
                }

                var name = new PersonName
                {
                    EntityType = s[2],
                    LastOrOrganizationName = s[3],
                    FirstName = s[4],
                    MiddleName = s[5],
                    IdQualifier = s[8],
                    Id = s[9]
                };
                switch (s[1])
                {
                    case "QC":
                        _claim.Patient = name;
                        break;
                    case "IL":
                        _claim.Insured = name;
                        break;
                    case "82":
                        _claim.RenderingProvider = name;
                        break;
                    default:
                        _claim.OtherEntities.Add(new KeyValuePair<string, PersonName>(s[1], name));
                        break;
                }
            }

            private void AddDate(X12Segment s)
            {
                var qualifier = s[1];
                switch (_section)
                {
                    case Section.Service when _service != null:
                        if (qualifier == "472" || qualifier == "150") _service.ServiceDate = OptionalDate(s[2]);
                        else if (qualifier == "151") _service.ServiceDateTo = OptionalDate(s[2]);
                        else _current.UnmappedSegments.Add(s);
                        return;
                    case Section.Claim when _claim != null:
                        if (qualifier == "232") _claim.StatementFrom = OptionalDate(s[2]);
                        else if (qualifier == "233") _claim.StatementTo = OptionalDate(s[2]);
                        else if (qualifier == "050") _claim.ReceivedDate = OptionalDate(s[2]);
                        else _current.UnmappedSegments.Add(s);
                        return;
                    default:
                        if (qualifier == "405") _current.ProductionDate = OptionalDate(s[2]);
                        else _current.UnmappedSegments.Add(s);
                        return;
                }
            }

            private void AddReference(X12Segment s)
            {
                var reference = new KeyValuePair<string, string>(s[1], s[2]);
                switch (_section)
                {
                    case Section.Service when _service != null:
                        if (s[1] == "6R") _service.LineItemControlNumber = s[2];
                        else _service.References.Add(reference);
                        return;
                    case Section.Claim when _claim != null:
                        _claim.References.Add(reference);
                        return;
                    case Section.Payer:
                        _current.Payer.References.Add(reference);
                        return;
                    case Section.Payee:
                        _current.Payee.References.Add(reference);
                        return;
                    default:
                        _current.UnmappedSegments.Add(s);
                        return;
                }
            }

            private void AddAmount(X12Segment s)
            {
                switch (_section)
                {
                    case Section.Service when _service != null:
                        if (s[1] == "B6") _service.AllowedAmount = Amount(s[2]);
                        else _current.UnmappedSegments.Add(s);
                        return;
                    case Section.Claim when _claim != null:
                        _claim.Amounts.Add(new KeyValuePair<string, decimal>(s[1], Amount(s[2])));
                        return;
                    default:
                        _current.UnmappedSegments.Add(s);
                        return;
                }
            }

            private void AddProviderAdjustments(X12Segment s)
            {
                _section = Section.Trailer;
                _claim = null;
                _service = null;
                var fiscalDate = OptionalDate(s[2]);

                // PLB03..PLB14: up to six pairs of reason composite (code:reference) and amount.
                for (var i = 3; i <= 13; i += 2)
                {
                    if (string.IsNullOrEmpty(s[i])) continue;
                    var reason = X12Tokenizer.SplitComponents(s[i], _result.Delimiters);
                    _current.ProviderAdjustments.Add(new ProviderAdjustment
                    {
                        ProviderId = s[1],
                        FiscalPeriodDate = fiscalDate,
                        ReasonCode = reason.Length > 0 ? reason[0] : string.Empty,
                        ReferenceId = reason.Length > 1 ? reason[1] : null,
                        Amount = Amount(s[i + 1])
                    });
                }
            }

            private void CheckCount(string element, string value, int actual, int index)
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared))
                {
                    _result.Issues.Add(new Remittance835Issue(IssueSeverity.Error, $"{element} is not a number.", null, index));
                    return;
                }

                if (declared != actual)
                    _result.Issues.Add(new Remittance835Issue(IssueSeverity.Error,
                        $"{element} says {declared}, found {actual}.", null, index));
            }

            private void Issue(IssueSeverity severity, string message, int? index)
            {
                _result.Issues.Add(new Remittance835Issue(severity, message, _current?.ControlNumber, index));
            }

            private static IEnumerable<string> NonEmpty(params string[] values)
            {
                foreach (var v in values)
                {
                    if (!string.IsNullOrEmpty(v)) yield return v;
                }
            }
        }

        internal static decimal Amount(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0m;
            if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var amount))
                return amount;
            throw new FormatException($"'{value}' is not a valid amount.");
        }

        internal static decimal? OptionalAmount(string value)
        {
            return string.IsNullOrEmpty(value) ? (decimal?)null : Amount(value);
        }

        internal static DateTime? OptionalDate(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var date))
                return date;
            throw new FormatException($"'{value}' is not a valid CCYYMMDD date.");
        }
    }
}
