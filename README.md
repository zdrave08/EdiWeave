[![Project Icon](./Assets/logo.png)](https://github.com/zdrave08/EdiWeave)

# EdiWeave.Next

[![CI](https://github.com/zdrave08/EdiWeave/actions/workflows/ci.yml/badge.svg)](https://github.com/zdrave08/EdiWeave/actions/workflows/ci.yml)

> **About this fork.** We maintain this fork of EdiWeave for our own projects. It targets .NET 8 and .NET 10, and our focus is HIPAA 5010 837P. Pull requests are welcome, but we can't guarantee support or response times. The original project is licensed under LGPL-3.0, and so is this fork.

EdiWeave.Next continues [EdiWeave](https://github.com/EdiWeave/EdiWeave), which is a hard-fork of the last open-source state of EdiFabric (now closed-source).

## Supported frameworks

| Package | Targets |
| --- | --- |
| `EdiWeave.Next.Core`, `EdiWeave.Next.Framework` | .NET Standard 2.0, .NET 8, .NET 10 |
| `EdiWeave.Next.Rules.Hipaa005010`, `EdiWeave.Next.Rules.Hipaa004010` | .NET Standard 2.0 |

.NET Framework 4.5, .NET Standard 1.6 and .NET Core 1.x/2.0 targets were removed; all are out of support. .NET Standard 2.0 still covers .NET Framework 4.6.1+.

## Compatibility with EdiWeave

Only the **package IDs** changed (`EdiWeave.*` → `EdiWeave.Next.*`). Assembly names and namespaces are unchanged (`EdiWeave.Core`, `EdiWeave.Framework`, `EdiWeave.Rules.*`), so existing code and rules loaded by assembly name keep working.

## What's included

- HIPAA 005010X222A1 **837P** and 004010X098A1 837 rules
- X12 002040, EDIFACT D00A, EANCOM D01B and VDA sample rules (used by the tests, not packaged)

There are **no 835 rules** in this repository.

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
