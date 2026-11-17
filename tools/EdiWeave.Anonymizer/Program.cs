// EdiWeave.Next - LGPL-3.0, see LICENSE in the repository root.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EdiWeave.Anonymizer;

const string KeyVariable = "EDI_ANON_KEY";

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: edi-anonymize <input file or folder> <output folder>");
    Console.Error.WriteLine($"The secret key is read from the {KeyVariable} environment variable (at least 16 characters).");
    Console.Error.WriteLine("Run it where the original files are. Only the output folder may leave that machine.");
    return 64;
}

var key = Environment.GetEnvironmentVariable(KeyVariable);
if (string.IsNullOrEmpty(key) || key.Length < 16)
{
    Console.Error.WriteLine($"Set {KeyVariable} to a secret of at least 16 characters. Keep it in the clinic; use the same key for every batch so pseudonyms stay consistent.");
    return 64;
}

var input = Path.GetFullPath(args[0]);
var outputDir = Path.GetFullPath(args[1]);
var files = Directory.Exists(input)
    ? Directory.GetFiles(input).OrderBy(f => f, StringComparer.Ordinal).ToArray()
    : File.Exists(input) ? new[] { input } : Array.Empty<string>();

if (files.Length == 0)
{
    Console.Error.WriteLine("No input files found.");
    return 66;
}

var inputDir = Directory.Exists(input) ? input : Path.GetDirectoryName(input);
if (string.Equals(inputDir?.TrimEnd(Path.DirectorySeparatorChar), outputDir.TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("The output folder must be different from the input folder.");
    return 64;
}

Directory.CreateDirectory(outputDir);
var anonymizer = new X12Anonymizer(Encoding.UTF8.GetBytes(key));
int written = 0, skipped = 0, blocked = 0, number = 0;

foreach (var file in files)
{
    number++;
    // Original file names often contain patient names: never print or reuse them.
    var label = $"file #{number}";
    string text;
    try
    {
        text = File.ReadAllText(file);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{label}: cannot read ({ex.GetType().Name}), skipped.");
        skipped++;
        continue;
    }

    if (!text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("ISA", StringComparison.Ordinal))
    {
        Console.WriteLine($"{label}: not an X12 file, skipped.");
        skipped++;
        continue;
    }

    AnonymizationResult result;
    try
    {
        result = anonymizer.Anonymize(text);
    }
    catch (FormatException ex)
    {
        Console.WriteLine($"{label}: {ex.Message} Skipped.");
        skipped++;
        continue;
    }

    if (result.Leaks.Count > 0)
    {
        // Values are never printed; only the segment IDs where they remained.
        Console.WriteLine($"{label}: BLOCKED, patient data still found in segment(s) {string.Join(", ", result.Leaks.Distinct())}. Nothing written. Review this file by hand.");
        blocked++;
        continue;
    }

    var payer = Slug(result.PayerName);
    var name = $"{result.TransactionType ?? "x12"}_{payer}_{number:000}.x12";
    File.WriteAllText(Path.Combine(outputDir, name), result.Text, new UTF8Encoding(false));
    Console.WriteLine($"{label} -> {name} ({result.Replacements} values replaced)");
    written++;
}

Console.WriteLine();
Console.WriteLine($"Written: {written}, blocked: {blocked}, skipped: {skipped}. Date shift: {anonymizer.DateShiftDays} days.");
Console.WriteLine("Before sending, open a few output files and check them by eye.");
return blocked > 0 ? 2 : 0;

static string Slug(string value)
{
    if (string.IsNullOrWhiteSpace(value)) return "unknown";
    var slug = Regex.Replace(value.Trim(), "[^A-Za-z0-9]+", "-").Trim('-');
    return slug.Length == 0 ? "unknown" : slug.Length > 30 ? slug.Substring(0, 30) : slug;
}
