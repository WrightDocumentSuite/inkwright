; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
IWF001  | Inkwright | Error   | A type marked with [PdfForm] and all its containing types must be declared partial.
IWF002  | Inkwright | Warning | A form member has a type that does not map to a field value.
IWF003  | Inkwright | Error   | A type shape cannot implement the generated form schema.
IWF004  | Inkwright | Error   | A form type cannot be constructed from its mapped values.
