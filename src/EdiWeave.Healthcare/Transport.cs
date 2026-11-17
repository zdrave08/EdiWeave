// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EdiWeave.Healthcare
{
    /// <summary>A file received from the clearinghouse or payer.</summary>
    public sealed class IncomingFile
    {
        /// <summary>File name at the source.</summary>
        public string Name { get; set; }

        /// <summary>File content.</summary>
        public byte[] Content { get; set; }

        /// <summary>Content as UTF-8 text.</summary>
        public string Text => Encoding.UTF8.GetString(Content ?? new byte[0]);
    }

    /// <summary>
    /// A way to exchange EDI files with a clearinghouse or payer. Implement it for SFTP, AS2 or an HTTPS API;
    /// <see cref="FolderTransport"/> covers drop folders and is also the easy way to test.
    /// </summary>
    public interface IEdiTransport
    {
        /// <summary>Sends a file.</summary>
        Task SendAsync(string fileName, byte[] content, CancellationToken cancellationToken = default);

        /// <summary>Lists the files waiting to be picked up.</summary>
        Task<IReadOnlyList<IncomingFile>> ReceiveAsync(CancellationToken cancellationToken = default);

        /// <summary>Tells the transport that a received file was processed, so it is not delivered again.</summary>
        Task MarkProcessedAsync(IncomingFile file, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Exchanges files through folders: <c>outbox</c> to send, <c>inbox</c> to receive, <c>processed</c> for files
    /// already handled. Files are written under a temporary name and renamed, so a reader never sees half a file.
    /// </summary>
    public sealed class FolderTransport : IEdiTransport
    {
        private readonly string _outbox;
        private readonly string _inbox;
        private readonly string _processed;

        /// <summary>Creates the transport. Folders are created when missing.</summary>
        public FolderTransport(string outboxFolder, string inboxFolder, string processedFolder)
        {
            _outbox = outboxFolder ?? throw new ArgumentNullException(nameof(outboxFolder));
            _inbox = inboxFolder ?? throw new ArgumentNullException(nameof(inboxFolder));
            _processed = processedFolder ?? throw new ArgumentNullException(nameof(processedFolder));
            Directory.CreateDirectory(_outbox);
            Directory.CreateDirectory(_inbox);
            Directory.CreateDirectory(_processed);
        }

        /// <inheritdoc />
        public async Task SendAsync(string fileName, byte[] content, CancellationToken cancellationToken = default)
        {
            var safe = SafeName(fileName);
            var target = Path.Combine(_outbox, safe);
            if (File.Exists(target)) throw new IOException("A file with this name is already waiting in the outbox.");
            var temp = target + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(content, 0, content.Length, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, target);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<IncomingFile>> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            var files = new List<IncomingFile>();
            foreach (var path in Directory.GetFiles(_inbox).Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new IncomingFile { Name = Path.GetFileName(path), Content = File.ReadAllBytes(path) });
            }
            return Task.FromResult<IReadOnlyList<IncomingFile>>(files);
        }

        /// <inheritdoc />
        public Task MarkProcessedAsync(IncomingFile file, CancellationToken cancellationToken = default)
        {
            var source = Path.Combine(_inbox, SafeName(file.Name));
            if (!File.Exists(source)) return Task.CompletedTask;
            var target = Path.Combine(_processed, SafeName(file.Name));
            if (File.Exists(target))
                target = Path.Combine(_processed, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + SafeName(file.Name));
            File.Move(source, target);
            return Task.CompletedTask;
        }

        private static string SafeName(string name)
        {
            // Only the file name: no folders, so a name cannot point outside the transport folders.
            var n = Path.GetFileName(name ?? string.Empty);
            if (string.IsNullOrWhiteSpace(n) || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid file name.", nameof(name));
            return n;
        }
    }

    /// <summary>Where a sent file stands.</summary>
    public enum SubmissionStatus
    {
        /// <summary>Sent, nothing back yet.</summary>
        Sent,

        /// <summary>The interchange or group was accepted (TA1 or 999).</summary>
        Acknowledged,

        /// <summary>The interchange or group was rejected (TA1 or 999). Nothing was processed: fix and resend.</summary>
        RejectedAtFileLevel,

        /// <summary>The group was accepted with errors or only partly accepted.</summary>
        PartiallyAccepted
    }

    /// <summary>One sent file. It holds no patient data: only counts, a total and control numbers.</summary>
    public sealed class SubmissionRecord
    {
        /// <summary>File name sent.</summary>
        public string FileName { get; set; }

        /// <summary>When it was sent (UTC).</summary>
        public DateTime SentAtUtc { get; set; }

        /// <summary>ISA13.</summary>
        public int InterchangeControlNumber { get; set; }

        /// <summary>GS06.</summary>
        public int GroupControlNumber { get; set; }

        /// <summary>Number of claims in the file.</summary>
        public int ClaimCount { get; set; }

        /// <summary>Sum of the claim charges.</summary>
        public decimal TotalCharge { get; set; }

        /// <summary>SHA-256 of the file as sent, to prove later what was sent.</summary>
        public string Sha256 { get; set; }

        /// <summary>Current status.</summary>
        public SubmissionStatus Status { get; set; }

        /// <summary>Short note, for example the 999 status code. Never patient data.</summary>
        public string Note { get; set; }

        /// <summary>Patient control numbers in the file (needed to match later answers).</summary>
        public List<string> PatientControlNumbers { get; set; } = new List<string>();
    }

    /// <summary>
    /// A JSON file listing everything sent, with the status taken from TA1 and 999 acknowledgements.
    /// Writes are atomic (temporary file and rename) and serialized within the process.
    /// </summary>
    public sealed class SubmissionLog
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
        private readonly object _gate = new object();
        private readonly string _path;

        /// <summary>Creates the log at the given file path.</summary>
        public SubmissionLog(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>Reads all records.</summary>
        public IReadOnlyList<SubmissionRecord> ReadAll()
        {
            lock (_gate) return Load();
        }

        /// <summary>Adds a record.</summary>
        public void Add(SubmissionRecord record)
        {
            lock (_gate)
            {
                var all = Load();
                all.Add(record);
                Save(all);
            }
        }

        /// <summary>
        /// Updates records from a TA1 (matched on ISA13) or a 999 (matched on GS06). Returns the number of records changed.
        /// </summary>
        public int Apply(AckResult acks)
        {
            if (acks == null) throw new ArgumentNullException(nameof(acks));
            lock (_gate)
            {
                var all = Load();
                var changed = 0;

                foreach (var ta1 in acks.InterchangeAcks)
                {
                    if (!int.TryParse(ta1.InterchangeControlNumber, out var icn)) continue;
                    foreach (var r in all.Where(x => x.InterchangeControlNumber == icn))
                    {
                        r.Status = ta1.IsAccepted ? SubmissionStatus.Acknowledged : SubmissionStatus.RejectedAtFileLevel;
                        r.Note = "TA1 " + ta1.AckCode + (string.IsNullOrEmpty(ta1.NoteCode) ? "" : " note " + ta1.NoteCode);
                        changed++;
                    }
                }

                foreach (var ack in acks.ImplementationAcks)
                {
                    if (!int.TryParse(ack.GroupControlNumber, out var gcn)) continue;
                    foreach (var r in all.Where(x => x.GroupControlNumber == gcn))
                    {
                        switch (ack.GroupStatus)
                        {
                            case "A": r.Status = SubmissionStatus.Acknowledged; break;
                            case "E":
                            case "P": r.Status = SubmissionStatus.PartiallyAccepted; break;
                            default: r.Status = SubmissionStatus.RejectedAtFileLevel; break;
                        }
                        r.Note = "999 " + ack.GroupStatus;
                        changed++;
                    }
                }

                if (changed > 0) Save(all);
                return changed;
            }
        }

        private List<SubmissionRecord> Load()
        {
            if (!File.Exists(_path)) return new List<SubmissionRecord>();
            var text = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text)) return new List<SubmissionRecord>();
            return JsonSerializer.Deserialize<List<SubmissionRecord>>(text) ?? new List<SubmissionRecord>();
        }

        private void Save(List<SubmissionRecord> records)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records, Json), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }
    }

    /// <summary>The outcome of <see cref="ClaimSubmitter.SubmitAsync"/>.</summary>
    public sealed class SubmissionResult
    {
        /// <summary>The file name sent.</summary>
        public string FileName { get; internal set; }

        /// <summary>The X12 text sent.</summary>
        public string Content { get; internal set; }

        /// <summary>The record added to the log.</summary>
        public SubmissionRecord Record { get; internal set; }

        /// <summary>Validation warnings (errors stop the submission with an exception).</summary>
        public List<ValidationIssue> Warnings { get; } = new List<ValidationIssue>();
    }

    /// <summary>The outcome of <see cref="ClaimSubmitter.ProcessResponsesAsync"/>.</summary>
    public sealed class ResponseBatch
    {
        /// <summary>Acknowledgements (TA1 and 999) read from incoming files.</summary>
        public List<AckResult> Acknowledgements { get; } = new List<AckResult>();

        /// <summary>277CA statuses.</summary>
        public List<ClaimStatus> ClaimStatuses { get; } = new List<ClaimStatus>();

        /// <summary>835 payment advices.</summary>
        public List<EdiWeave.Hipaa835.RemittanceAdvice> Remittances { get; } = new List<EdiWeave.Hipaa835.RemittanceAdvice>();

        /// <summary>271 eligibility answers.</summary>
        public List<EligibilityResponse> Eligibility { get; } = new List<EligibilityResponse>();

        /// <summary>Files that were not recognized. They are left unprocessed in the inbox.</summary>
        public List<string> UnknownFiles { get; } = new List<string>();

        /// <summary>Submission records changed by acknowledgements.</summary>
        public int SubmissionsUpdated { get; internal set; }
    }

    /// <summary>
    /// Ties the pieces together: validates and writes a batch, sends it, records it in the submission log and the
    /// audit log, and later reads the answers that come back.
    /// </summary>
    public sealed class ClaimSubmitter
    {
        private readonly IEdiTransport _transport;
        private readonly SubmissionLog _log;
        private readonly AuditLog _audit;

        /// <summary>Creates the submitter. The audit log is optional.</summary>
        public ClaimSubmitter(IEdiTransport transport, SubmissionLog log, AuditLog audit = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _audit = audit;
        }

        /// <summary>
        /// Validates, writes and sends the batch. Throws <see cref="ClaimValidationException"/> before anything is
        /// sent when the batch has errors. The file name is built from the control number, never from patient data.
        /// </summary>
        public async Task<SubmissionResult> SubmitAsync(Claim837PBatch batch, X12Options options, string user,
            CancellationToken cancellationToken = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (options == null) throw new ArgumentNullException(nameof(options));

            var issues = Claim837PValidator.Validate(batch);
            if (!Claim837PValidator.IsValid(issues))
            {
                _audit?.Record(user, "claims.submit", "batch", "rejected-validation", issues.Count(i => i.Severity == EdiWeave.Hipaa835.IssueSeverity.Error) + " errors");
                throw new ClaimValidationException(issues);
            }

            var content = Claim837PWriter.Write(batch, options);
            var bytes = new UTF8Encoding(false).GetBytes(content);
            var fileName = "837P_" + options.InterchangeControlNumber.ToString("D9") + ".x12";

            if (_log.ReadAll().Any(r => r.InterchangeControlNumber == options.InterchangeControlNumber))
                throw new InvalidOperationException("This interchange control number was already used. Use a new one for every file.");

            await _transport.SendAsync(fileName, bytes, cancellationToken).ConfigureAwait(false);

            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();

            var record = new SubmissionRecord
            {
                FileName = fileName,
                SentAtUtc = DateTime.UtcNow,
                InterchangeControlNumber = options.InterchangeControlNumber,
                GroupControlNumber = options.GroupControlNumber,
                ClaimCount = batch.Claims.Count,
                TotalCharge = batch.Claims.Sum(c => c.TotalCharge),
                Sha256 = hash,
                Status = SubmissionStatus.Sent,
                PatientControlNumbers = batch.Claims.Select(c => c.PatientControlNumber).ToList()
            };
            _log.Add(record);
            _audit?.Record(user, "claims.submit", fileName, "sent", batch.Claims.Count + " claims");

            var result = new SubmissionResult { FileName = fileName, Content = content, Record = record };
            result.Warnings.AddRange(issues);
            return result;
        }

        /// <summary>
        /// Reads every incoming file, sorts it by type, applies TA1 and 999 results to the submission log and marks
        /// recognized files as processed. Unknown files stay where they are.
        /// </summary>
        public async Task<ResponseBatch> ProcessResponsesAsync(string user, CancellationToken cancellationToken = default)
        {
            var batch = new ResponseBatch();
            var files = await _transport.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            foreach (var file in files)
            {
                var text = file.Text;
                switch (EdiClassifier.Classify(text))
                {
                    case EdiKind.Ack999:
                    case EdiKind.InterchangeAckTa1:
                        var ack = AckReader.Read(text);
                        batch.Acknowledgements.Add(ack);
                        batch.SubmissionsUpdated += _log.Apply(ack);
                        break;
                    case EdiKind.ClaimStatus277:
                        batch.ClaimStatuses.AddRange(ClaimStatusReader.Read(text).Claims);
                        break;
                    case EdiKind.Remittance835:
                        batch.Remittances.AddRange(EdiWeave.Hipaa835.Remittance835Reader.Read(text).Transactions);
                        break;
                    case EdiKind.Eligibility271:
                        batch.Eligibility.AddRange(EligibilityReader.Read(text));
                        break;
                    default:
                        batch.UnknownFiles.Add(file.Name);
                        _audit?.Record(user, "responses.read", file.Name, "unknown-type", null);
                        continue;
                }

                _audit?.Record(user, "responses.read", file.Name, "processed", EdiClassifier.Classify(text).ToString());
                await _transport.MarkProcessedAsync(file, cancellationToken).ConfigureAwait(false);
            }

            return batch;
        }
    }
}
