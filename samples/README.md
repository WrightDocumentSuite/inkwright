# Inkwright runnable examples

Run from the repository root with the SDK pinned in `global.json`:

```sh
dotnet run -c Release --project samples/Inkwright.Samples -- ./sample-output
```

The optional first argument is the output directory; use an empty directory to keep results separate from your files.

The existing examples run first, followed by these self-contained scenarios. Each new scenario creates its inputs, reloads or checks its output, and throws if the demonstrated result changes. All captions and messages use English.

| Example | Scenario | Output |
| --- | --- | --- |
| [MergeAndSplit](Inkwright.Samples/MergeAndSplit.cs) | Merge two generated documents and extract the second page using a zero-based index; verify source and result page counts. | `merged-pack.pdf; extracted-checklist.pdf` |
| [NavigationAndAttachments](Inkwright.Samples/NavigationAndAttachments.cs) | Attach a source CSV, add a page bookmark, reload the PDF and verify the attachment bytes. | `navigable-report.pdf` |
| [PasswordProtection](Inkwright.Samples/PasswordProtection.cs) | Save with AES-256 encryption and sample passwords, then reopen and verify readable text. | `password-protected.pdf` |

Each source file exposes `RunAsync(directory)` and can also be called independently. See the [getting-started guide](../docs/getting-started.md) for package installation. The same new method bodies are shown in the [website playground](https://wrightsuite.com/#code-playground).

Page extraction uses zero-based indices and copies pages without consuming the sources. Attachment bytes are checked after reload. Encryption passwords are fixed demonstration data; applications should provide their own secrets and policy.
