# HIPAA security checklist for software built on EdiWeave.Next

EdiWeave.Next.Healthcare gives you building blocks (audit log, redaction, file encryption). **A library cannot make software HIPAA compliant.** Compliance depends on how you run the system, who has access, and your policies. This page lists what the library does, and what you still have to do.

## What the library gives you

| Need | Building block | Notes |
| --- | --- | --- |
| Know who did what | `AuditLog` | Append-only JSON lines with a hash chain. `Verify()` detects edited, removed or reordered lines. Never contains patient data. |
| Keep patient data out of logs and tickets | `PhiRedactor.Redact` | Masks names, member IDs, addresses, phone numbers, claim numbers and free text; keeps structure, codes, amounts and provider IDs. Date of birth keeps only the year. |
| Protect files at rest | `EdiFileProtector` | AES-256-GCM. Password (PBKDF2, 600000 iterations) or a 32-byte key from a vault. Tampering is detected. |
| Prove what was sent | `SubmissionLog` | File name, control numbers, claim count, total, SHA-256. No patient data. |
| Avoid sending bad data | `Claim837PValidator` | Messages never repeat patient values. |

## What you still have to do

1. **Encrypt in transit.** Use SFTP, AS2 or HTTPS with current TLS. `FolderTransport` is a drop folder: place it on an encrypted volume with restricted access.
2. **Encrypt at rest.** Use full-disk or database encryption, and `EdiFileProtector` for archived 837/835 files. Keep keys in a vault, not next to the data.
3. **Access control.** Give each person their own login, the minimum access they need, and remove access when they leave. Pass the real user name to `ClaimSubmitter` so the audit log is meaningful.
4. **Store the audit log somewhere the application account cannot rewrite** (write-once storage, or ship it to a log server). Keep it for at least 6 years.
5. **Backups.** Encrypted, tested restores, kept offsite.
6. **Never use real data in tests, tickets, chat or git.** Use `tools/EdiWeave.Anonymizer` for fixtures, and `PhiRedactor` for error reports.
7. **Business associate agreements.** You need one with every party that handles patient data for you: clearinghouse, hosting provider, backup provider, support vendors.
8. **Risk analysis and policies.** HIPAA requires a documented security risk analysis, workforce training, an incident response plan and breach notification procedures. Review them at least once a year.
9. **Retention and disposal.** Decide how long to keep claims and remittances, and securely delete after that.
10. **Patch and monitor.** Keep the operating system, .NET runtime and dependencies up to date; alert on repeated failed logins and on `claims.submit` outside working hours.

## Reading the answers

- **TA1 rejected** or **999 rejected**: nothing was processed. Fix the file and send it again with a **new** interchange control number.
- **277CA rejected (A3, A4, A6, A7, A8)**: the payer never adjudicated the claim. Correct the data and resubmit; do not wait for a payment.
- **835 denied (CLP02 = 4)**: processed and denied. Read the CAS reason codes before appealing.
- Match everything by the patient control number (`CLM01` = `TRN02` in the 277CA = `CLP01` in the 835). Use a number that identifies the claim in your system but is **not** a social security number or other direct identifier.

This page is guidance, not legal advice. Ask your compliance officer or counsel about your specific obligations.
