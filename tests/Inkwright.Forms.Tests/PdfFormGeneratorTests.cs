using System.Collections.Immutable;
using Inkwright.Forms;
using Inkwright.Forms.Generator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Inkwright.Forms.Tests;

public class PdfFormGeneratorTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    [Fact]
    public void EscapesReservedAndContextualKeywordIdentifiers()
    {
        const string source = """
            using Inkwright.Forms;

            namespace @namespace;

            [PdfForm]
            public partial record @event(string @class, string @required);
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        string generated = Assert.Single(generation.GeneratedSources);
        Assert.Contains("namespace @namespace;", generated, StringComparison.Ordinal);
        Assert.Contains("partial record @event", generated, StringComparison.Ordinal);
        Assert.Contains("value.@class", generated, StringComparison.Ordinal);
        Assert.Contains("value.@required", generated, StringComparison.Ordinal);
        Assert.Contains("\"class\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"required\"", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsNestedGenericFormsAndReadonlyRecordStructs()
    {
        const string source = """
            using Inkwright.Forms;

            public partial interface Outer<TOuter> where TOuter : class
            {
                [PdfForm]
                public readonly partial record struct Inner<TValue>(
                    [property: PdfField("value")] string Value)
                    where TValue : unmanaged;
            }
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        string generated = Assert.Single(generation.GeneratedSources);
        Assert.Contains("partial interface Outer<TOuter>", generated, StringComparison.Ordinal);
        Assert.Contains("partial record struct Inner<TValue>", generated, StringComparison.Ordinal);
        Assert.Contains("IPdfFormSchema<Inner<TValue>>", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsIwf001ForANonPartialForm()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public class NonPartialForm
            {
                public string Name { get; set; } = "";
            }
            """;

        const string nestedSource = """
            using Inkwright.Forms;

            public class NonPartialContainer
            {
                [PdfForm]
                public partial class NestedForm
                {
                    public string Name { get; set; } = "";
                }
            }
            """;

        Generation generation = Run(source);
        Generation nestedGeneration = Run(nestedSource);

        Diagnostic diagnostic = Assert.Single(generation.Diagnostics, item => item.Id == "IWF001");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Empty(generation.GeneratedSources);
        Assert.Contains(nestedGeneration.Diagnostics, item => item.Id == "IWF001");
        Assert.Empty(nestedGeneration.GeneratedSources);
    }

    [Fact]
    public void ReportsIwf002AndOmitsAnUnsupportedMember()
    {
        const string source = """
            using System;
            using Inkwright.Forms;

            [PdfForm]
            public partial class UnsupportedMemberForm
            {
                public Uri Value { get; set; } = new("https://example.test");
            }
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        Diagnostic diagnostic = Assert.Single(generation.Diagnostics, item => item.Id == "IWF002");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.DoesNotContain("value.Value", Assert.Single(generation.GeneratedSources), StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesControlCharactersInAttributeStrings()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public partial record ControlCharacterForm(
                [property: PdfField("line\n\t\u0001\"\\", Format = "0.0\r\n\u2028")] decimal Value);
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        string generated = Assert.Single(generation.GeneratedSources);
        Assert.Contains("\\n", generated, StringComparison.Ordinal);
        Assert.Contains("\\t", generated, StringComparison.Ordinal);
        Assert.Contains("\\u0001", generated, StringComparison.Ordinal);
        Assert.Contains("\\u2028", generated, StringComparison.Ordinal);
        Assert.Contains("\"line\\n\\t\\u0001\\\"\\\\\"", generated, StringComparison.Ordinal);
        Assert.Contains("\"0.0\\r\\n\\u2028\"", generated, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', generated);
        Assert.DoesNotContain('\u2028', generated);
    }

    [Fact]
    public void SupportsRequiredProperties()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public partial class RequiredForm
            {
                public required string Name { get; init; }
            }
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        Assert.Contains("Name = _field0", Assert.Single(generation.GeneratedSources), StringComparison.Ordinal);
    }

    [Fact]
    public void FullyQualifiesFrameworkAndFormsTypes()
    {
        const string source = """
            namespace Shadows;

            public sealed class PdfForm;
            public interface IPdfFormSchema<T>;

            [global::Inkwright.Forms.PdfFormAttribute]
            public partial record ShadowedNames(int Value);
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        string generated = Assert.Single(generation.GeneratedSources);
        Assert.Contains("global::Inkwright.Forms.IPdfFormSchema", generated, StringComparison.Ordinal);
        Assert.Contains("global::Inkwright.Forms.PdfForm", generated, StringComparison.Ordinal);
        Assert.Contains("global::System.Globalization.CultureInfo", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsClearDiagnosticsForUnsupportedShapesAndConstruction()
    {
        const string staticSource = """
            using Inkwright.Forms;

            [PdfForm]
            public static partial class StaticForm
            {
            }
            """;

        const string readOnlySource = """
            using Inkwright.Forms;

            [PdfForm]
            public partial class ReadOnlyForm
            {
                private ReadOnlyForm(int name) { }
                public string Name => "";
            }
            """;

        const string unsupportedRequiredSource = """
            using System;
            using Inkwright.Forms;

            [PdfForm]
            public partial class UnsupportedRequiredForm
            {
                public required Uri Value { get; init; }
            }
            """;

        Generation staticGeneration = Run(staticSource);
        Generation readOnlyGeneration = Run(readOnlySource);
        Generation unsupportedRequiredGeneration = Run(unsupportedRequiredSource);

        Assert.Contains(staticGeneration.Diagnostics, item => item.Id == "IWF003");
        Assert.Empty(staticGeneration.GeneratedSources);
        Assert.Contains(readOnlyGeneration.Diagnostics, item => item.Id == "IWF004");
        Assert.Empty(readOnlyGeneration.GeneratedSources);
        Assert.Contains(unsupportedRequiredGeneration.Diagnostics, item => item.Id == "IWF002");
        Assert.Contains(unsupportedRequiredGeneration.Diagnostics, item => item.Id == "IWF004");
        Assert.Empty(unsupportedRequiredGeneration.GeneratedSources);
    }

    [Fact]
    public void HonorsIgnoreOnPositionalRecordParameters()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public partial record IgnoredConstructorMember(
                [PdfFieldIgnore] string Secret,
                string Name);
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        string generated = Assert.Single(generation.GeneratedSources);
        Assert.DoesNotContain("form.Set(\"Secret\"", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("form[\"Secret\"]", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Secret\",", generated, StringComparison.Ordinal);
        Assert.Contains("Secret: default!", generated, StringComparison.Ordinal);
        Assert.Contains("Name: _field0", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesDistinctHintsForNamesThatResembleEscapedMetadata()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public partial record A_002B_B(string Value);

            public partial class A
            {
                [PdfForm]
                public partial record B(string Value);
            }
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        Assert.Equal(2, generation.GeneratedSources.Length);
        Assert.DoesNotContain(generation.Diagnostics, diagnostic => diagnostic.Id == "CS8785");
    }

    [Fact]
    public void OmitsUnmappedOptionalConstructorParameters()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public partial class OptionalConstructorForm
            {
                public OptionalConstructorForm(int ignored = 42) { }
                public string Name { get; set; } = "";
            }
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        Assert.DoesNotContain(generation.Diagnostics, diagnostic => diagnostic.Id == "IWF004");
        Assert.Contains("Name = _field0", Assert.Single(generation.GeneratedSources), StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsImplicitConstructorArgumentConversions()
    {
        const string source = """
            using Inkwright.Forms;

            [PdfForm]
            public partial class ConvertibleConstructorForm
            {
                public ConvertibleConstructorForm(long value) => Value = checked((int)value);
                public int Value { get; }
            }
            """;

        Generation generation = Run(source);

        AssertCompiles(generation);
        Assert.Contains("value: _field0", Assert.Single(generation.GeneratedSources), StringComparison.Ordinal);
    }

    private static Generation Run(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, ParseOptions);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "GeneratorTests",
            [tree],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PdfFormGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out _);
        GeneratorDriverRunResult result = driver.GetRunResult();

        return new Generation(
            output,
            result.Diagnostics,
            result.GeneratedTrees.Select(generated => generated.GetText().ToString()).ToImmutableArray());
    }

    private static void AssertCompiles(Generation generation)
    {
        Diagnostic[] errors = generation.Output.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        string trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The runtime did not provide trusted platform assemblies.");

        return trustedAssemblies
            .Split(Path.PathSeparator)
            .Append(typeof(PdfFormAttribute).Assembly.Location)
            .Append(typeof(PdfDocument).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToImmutableArray<MetadataReference>();
    }

    private sealed record Generation(
        Compilation Output,
        ImmutableArray<Diagnostic> Diagnostics,
        ImmutableArray<string> GeneratedSources);
}
