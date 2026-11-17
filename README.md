[![Project Icon](./Assets/logo.png)](https://github.com/zdrave08/EdiWeave)

# EdiWeave.Next

[![CI](https://github.com/zdrave08/EdiWeave/actions/workflows/ci.yml/badge.svg)](https://github.com/zdrave08/EdiWeave/actions/workflows/ci.yml)

> **About this fork.** We maintain this fork of EdiWeave for our own projects. It targets .NET 8 and .NET 10, and our focus is HIPAA 5010 837P and 835. Pull requests are welcome, but we can't guarantee support or response times. The original project is licensed under LGPL-3.0, and so is this fork.

EdiWeave.Next continues [EdiWeave](https://github.com/EdiWeave/EdiWeave), which is a hard-fork of the last open-source state of EdiFabric (now closed-source).

## Supported frameworks

| Package | Targets |
| --- | --- |
| `EdiWeave.Next.Core`, `EdiWeave.Next.Framework` | .NET Standard 2.0, .NET 8, .NET 10 |
| `EdiWeave.Next.Rules.Hipaa005010`, `EdiWeave.Next.Rules.Hipaa004010` | .NET Standard 2.0 |
| `EdiWeave.Next.Hipaa835` | .NET Standard 2.0, .NET 8, .NET 10 |
| `EdiWeave.Next.Healthcare` | .NET 8, .NET 10 |

.NET Framework 4.5, .NET Standard 1.6 and .NET Core 1.x/2.0 targets were removed; all are out of support. .NET Standard 2.0 still covers .NET Framework 4.6.1+.

## Compatibility with EdiWeave

Only the **package IDs** changed (`EdiWeave.*` → `EdiWeave.Next.*`). Assembly names and namespaces are unchanged (`EdiWeave.Core`, `EdiWeave.Framework`, `EdiWeave.Rules.*`), so existing code and rules loaded by assembly name keep working.

## What's included

- HIPAA 005010X222A1 **837P** and 004010X098A1 837 rules
- X12 002040, EDIFACT D00A, EANCOM D01B and VDA sample rules (used by the tests, not packaged)

- **835 reader** (`EdiWeave.Next.Hipaa835`, 005010X221A1): payment (BPR/TRN), payer and payee, claims (CLP), service lines (SVC), CAS adjustments, PLB provider adjustments, plus balancing checks

The 835 reader is a separate, lightweight package that does not depend on the rules engine. It reads remittance files; it does not write them.

```csharp
using EdiWeave.Hipaa835;

var result = Remittance835Reader.Read(File.OpenRead("remit.835"));
foreach (var issue in result.Issues) Console.WriteLine(issue);          // envelope, counts, bad values

foreach (var advice in result.Transactions)
{
    var balancing = RemittanceBalancing.Check(advice);                  // empty when it balances
    Console.WriteLine($"{advice.TraceNumber}: {advice.Payment.TotalPayment} for {advice.Claims.Count} claims");

    foreach (var claim in advice.Claims)
    foreach (var line in claim.ServiceLines)
        Console.WriteLine($"{claim.PatientControlNumber} {line.LineItemControlNumber} {line.ProcedureCode} paid {line.Paid}");
}
```

Notes:
- The reader never throws on bad content; problems are listed in `Issues`. It accepts an ISA whose fields are not padded to fixed width, any delimiters, and line breaks between segments.
- Amounts and dates are parsed with the invariant culture.
- `ServicePayment.LineItemControlNumber` is the 835 `REF*6R`, which echoes the 837 `REF*6R`. Use it to match payments to the lines you billed.
- Segments the model does not map (for example MIA, MOA, TS3) are kept in `UnmappedSegments`.
- Test files under `tests/EdiWeave.UnitTests.Hipaa835/Edi` are synthetic. Never commit real remittance files: they contain patient data.

## Claim workflow for medical software (EdiWeave.Next.Healthcare)

`EdiWeave.Next.Healthcare` covers the whole claim cycle for HIPAA 5010 professional claims. It is lightweight: it does not need the rules engine, and it works with the 835 reader.

| Step | Class | What it does |
| --- | --- | --- |
| Build the 837P | `Claim837PBatch`, `Claim837PWriter` | Writes 005010X222A1 from plain objects: submitter, billing provider, subscribers, dependents, claims, diagnoses, service lines. Groups claims into the right HL loops. The output is checked against the rules engine in the tests. |
| Validate before sending | `Claim837PValidator`, `Identifiers` | NPI check digit, ICD-10 and CPT/HCPCS shape, totals, diagnosis pointers, dates, addresses, duplicate control numbers. Messages never contain patient values. |
| Check eligibility | `Eligibility270Writer`, `EligibilityReader` | Writes 270 (005010X279A1) and reads 271: active or inactive, co-payment, deductible, co-insurance, out-of-pocket, rejections with follow-up action. |
| Read what came back | `AckReader`, `ClaimStatusReader` | TA1 (envelope), 999 (syntax, with segment and element errors) and 277CA (accepted, rejected, pending, finalized, per claim and per line). `EdiClassifier` tells you what an incoming file is. |
| Match everything | `ClaimReconciler` | Joins your 837P claims with 277CA and 835: paid, partly paid, denied, reversed, rejected, unmatched payments, line by line, with notes when the amounts do not add up. |
| Send and track | `IEdiTransport`, `FolderTransport`, `SubmissionLog`, `ClaimSubmitter` | Validates, writes, sends and records each file (no patient data in the log), then reads incoming files and updates the status from TA1 and 999. Implement `IEdiTransport` for SFTP, AS2 or an HTTPS API. |
| Protect | `AuditLog`, `PhiRedactor`, `EdiFileProtector` | Tamper-evident audit log, masking of patient data for logs and tickets, AES-256-GCM file encryption. See [Wiki/HIPAA-Security-Checklist.md](Wiki/HIPAA-Security-Checklist.md) for what the library covers and what you still have to do. |

```csharp
using EdiWeave.Healthcare;

var batch = new Claim837PBatch { /* submitter, receiver, billing provider */ };
batch.Claims.Add(claim);                                          // Claim837P with diagnoses and service lines

var options = new X12Options { SenderId = "MYCLINIC", ReceiverId = "CLEARINGHOUSE", InterchangeControlNumber = 17 };
var transport = new FolderTransport("outbox", "inbox", "processed");
var submitter = new ClaimSubmitter(transport, new SubmissionLog("log/submissions.json"), new AuditLog("log/audit.log"));

var sent = await submitter.SubmitAsync(batch, options, user: "jane");      // validates, writes, sends, records
var responses = await submitter.ProcessResponsesAsync(user: "jane");       // 999, 277CA, 835, 271
var outcome = ClaimReconciler.Reconcile(batch.Claims, responses.Remittances, responses.ClaimStatuses);
```

The writer only produces a file when validation finds no errors, and every file needs a new interchange control number. Use `ISA15 = T` (`Production = false`) until the clearinghouse approves your test files.

## Anonymizing real files for tests (tools/EdiWeave.Anonymizer)

Real 837/835 files contain patient data and must never be committed or sent outside the clinic. The `edi-anonymize` tool makes copies that are safe to use as test fixtures.

Run it **on the machine where the original files are**:

```
set EDI_ANON_KEY=<secret of at least 16 characters, kept in the clinic>        (Windows)
export EDI_ANON_KEY=<secret of at least 16 characters, kept in the clinic>     (Linux/macOS)
dotnet run --project tools/EdiWeave.Anonymizer -c Release -- <input file or folder> <output folder>
```

What it does:

| Data | Treatment |
| --- | --- |
| Patient and subscriber names, member IDs (NM1*IL/QC/74/QD) | Format-preserving pseudonym: letters stay letters, digits stay digits, dashes kept |
| Patient control and payer claim numbers (CLM01, CLP01, CLP07), REF*D9/6R/SY/1W/IG/… | Format-preserving pseudonym |
| Group number (SBR03), group name (SBR04) | Pseudonym / "SYNTHETIC GROUP" |
| Patient street address (N3), postal code (N4) | Replaced; city, state and country kept |
| Date of birth (DMG) | Year only (YYYY0101) |
| Service and other dates (DTP, DTM) | Shifted by the same number of days for every file (intervals kept) |
| Free text (NTE, K3, MSG) | "REDACTED" |
| Codes, amounts, providers, payers, ISA/GS | Kept |

- Pseudonyms are deterministic for a given key: the same claim number gets the same pseudonym in the 837 and the matching 835. Use the same key for every batch.
- After replacing, the tool searches each output for the original values. If any remain (for example in a segment it does not know), **the file is not written** and the tool exits with code 2. Only segment IDs are printed, never values.
- Output files are named by transaction type and payer (`837_<payer>_001.x12`). Original file names, which often contain patient names, are never printed or reused.
- Diagnosis codes are kept. In a small population a rare diagnosis with sex and year of birth can still identify someone: review the output before it leaves the clinic.

## Non-padded ISA

The X12 standard requires a fixed-width ISA (106 characters). Some trading partners send it with fields not padded (for example `ISA*00**00**ZZ*SENDER*ZZ*RECEIVER*...`). `X12Reader` accepts that form when the ISA starts a segment and every field has the shape the standard requires (two-character qualifiers, six-digit date, four-digit time, numeric control number, and so on). Text such as "LISA" inside data is never taken for a header.

## Building and testing

```
dotnet build EdiWeave.sln -c Release
dotnet test EdiWeave.sln -c Release
```

Tests run on .NET 8 and .NET 10. The sample EDI files are stored with CRLF line endings (`.gitattributes`: `-text`); tests use `CommonHelper.WindowsNewLine` instead of `Environment.NewLine` so they pass on Linux and macOS.

## Versioning

This fork starts at **9.0.0**, above the last upstream `EdiWeave.Framework` 8.2.0. Tags: `v9.0.0`, `v9.0.1`, …

## Migrating from EdiFabric

This project keeps API compatibility with the last open-source EdiFabric where possible. Replace package references with `EdiWeave.Next.*`, then find and replace `EdiFabric` with `EdiWeave` in your code.

**Note**: If you are using the latest version of the EdiFabric class-based definitions, find and remove the `Id` property and the attributes on it. Otherwise parsing fails.

## License

LGPL-3.0, see [LICENSE](LICENSE). You can use the library from closed-source applications; changes to the library itself must be published under the same license.
