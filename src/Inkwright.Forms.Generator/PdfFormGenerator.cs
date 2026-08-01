using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Inkwright.Forms.Generator;

/// <summary>
/// Generates the mapping between a type and the fields of an interactive form. The alternative is
/// reflection, which costs a lookup per property at run time and stops working the moment the
/// application is trimmed; a generator moves the same work to compile time and keeps the names
/// checkable by the compiler.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PdfFormGenerator : IIncrementalGenerator
{
    private const string FormAttribute = "Inkwright.Forms.PdfFormAttribute";
    private const string FieldAttribute = "Inkwright.Forms.PdfFieldAttribute";
    private const string IgnoreAttribute = "Inkwright.Forms.PdfFieldIgnoreAttribute";

    private static readonly DiagnosticDescriptor MustBePartial = new(
        "IWF001",
        "A form type must be partial",
        "'{0}' is marked with [PdfForm] but is not declared partial, so the mapping cannot be generated",
        "Inkwright",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedType = new(
        "IWF002",
        "A form member has an unsupported type",
        "'{0}.{1}' is of type '{2}', which does not map to a form field value",
        "Inkwright",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<FormModel?> models = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FormAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (syntax, _) => Build(syntax))
            .Where(static model => model is not null);

        context.RegisterSourceOutput(models, static (production, model) => Emit(production, model!));
    }

    private static FormModel? Build(GeneratorAttributeSyntaxContext syntax)
    {
        if (syntax.TargetSymbol is not INamedTypeSymbol type || syntax.TargetNode is not TypeDeclarationSyntax declaration)
            return null;

        bool partial = declaration.Modifiers.Any(modifier => modifier.ValueText == "partial");

        var members = ImmutableArray.CreateBuilder<FieldModel>();
        IMethodSymbol? primaryConstructor = FindPrimaryConstructor(type);

        foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public)
                continue;

            if (HasAttribute(property, IgnoreAttribute))
                continue;

            if (property.GetMethod is null)
                continue;

            IParameterSymbol? parameter = primaryConstructor?.Parameters
                .FirstOrDefault(p => string.Equals(p.Name, property.Name, System.StringComparison.OrdinalIgnoreCase));

            (string? name, string? format) = ReadFieldAttribute(property) is { } fromProperty
                ? fromProperty
                : parameter is not null && ReadFieldAttribute(parameter) is { } fromParameter
                    ? fromParameter
                    : (null, null);

            ValueKind kind = Classify(property.Type, out bool nullable, out string typeName);

            members.Add(new FieldModel(
                property.Name,
                name ?? property.Name,
                format,
                kind,
                nullable,
                typeName,
                property.SetMethod is not null && property.SetMethod.DeclaredAccessibility == Accessibility.Public,
                parameter?.Ordinal ?? -1));
        }

        // A form type is often nested inside the class that uses it, so the generated half has to be
        // wrapped in the same chain of partial declarations.
        var containers = ImmutableArray.CreateBuilder<string>();
        for (INamedTypeSymbol? container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            string keyword = container.IsRecord
                ? container.IsValueType ? "record struct" : "record"
                : container.IsValueType ? "struct" : "class";

            containers.Insert(0, $"{keyword} {container.Name}");

            foreach (SyntaxReference reference in container.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax containerSyntax &&
                    !containerSyntax.Modifiers.Any(modifier => modifier.ValueText == "partial"))
                {
                    partial = false;
                }
            }
        }

        return new FormModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            DeclarationKeyword(type, declaration),
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            partial,
            primaryConstructor is not null && members.All(m => m.ConstructorOrdinal >= 0 || m.HasSetter),
            members.ToImmutable(),
            containers.ToImmutable(),
            declaration.Identifier.GetLocation());
    }

    private static IMethodSymbol? FindPrimaryConstructor(INamedTypeSymbol type)
    {
        IEnumerable<IPropertySymbol> properties = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public);

        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.Length == 0 || constructor.DeclaredAccessibility != Accessibility.Public)
                continue;

            bool matches = constructor.Parameters.All(parameter =>
                properties.Any(property => string.Equals(property.Name, parameter.Name, System.StringComparison.OrdinalIgnoreCase)));

            if (matches)
                return constructor;
        }

        return null;
    }

    private static (string? Name, string? Format)? ReadFieldAttribute(ISymbol symbol)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != FieldAttribute)
                continue;

            string? name = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value as string
                : null;

            string? format = attribute.NamedArguments
                .FirstOrDefault(pair => pair.Key == "Format").Value.Value as string;

            return (name, format);
        }

        return null;
    }

    private static bool HasAttribute(ISymbol symbol, string metadataName) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string DeclarationKeyword(INamedTypeSymbol type, TypeDeclarationSyntax declaration)
    {
        string keyword = declaration.Keyword.ValueText;
        return type.IsRecord && keyword != "record" ? "record " + keyword : keyword;
    }

    private static ValueKind Classify(ITypeSymbol type, out bool nullable, out string typeName)
    {
        nullable = false;
        ITypeSymbol effective = type;

        if (type is INamedTypeSymbol { IsGenericType: true } named &&
            named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            nullable = true;
            effective = named.TypeArguments[0];
        }
        else if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullable = true;
        }

        typeName = effective.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (effective.TypeKind == TypeKind.Enum)
            return ValueKind.Enum;

        return effective.SpecialType switch
        {
            SpecialType.System_String => ValueKind.String,
            SpecialType.System_Boolean => ValueKind.Boolean,
            SpecialType.System_Int16 or SpecialType.System_Int32 or SpecialType.System_Int64 or
                SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64 => ValueKind.Integer,
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => ValueKind.Number,
            SpecialType.System_DateTime => ValueKind.DateTime,
            _ => typeName switch
            {
                "global::System.DateOnly" => ValueKind.DateOnly,
                "global::System.DateTimeOffset" => ValueKind.DateTimeOffset,
                "global::System.Guid" => ValueKind.Guid,
                _ => ValueKind.Unsupported,
            },
        };
    }

    private static void Emit(SourceProductionContext production, FormModel model)
    {
        if (!model.IsPartial)
        {
            production.ReportDiagnostic(Diagnostic.Create(MustBePartial, model.Location, model.TypeName));
            return;
        }

        foreach (FieldModel field in model.Fields)
        {
            if (field.Kind == ValueKind.Unsupported)
            {
                production.ReportDiagnostic(
                    Diagnostic.Create(UnsupportedType, model.Location, model.TypeName, field.MemberName, field.TypeName));
            }
        }

        ImmutableArray<FieldModel> mapped = model.Fields.Where(f => f.Kind != ValueKind.Unsupported).ToImmutableArray();

        var source = new StringBuilder(4096);
        source.AppendLine("// <auto-generated by Inkwright.Forms.Generator />");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        source.AppendLine("using System;");
        source.AppendLine("using System.Collections.Generic;");
        source.AppendLine("using System.Globalization;");
        source.AppendLine("using Inkwright.Forms;");
        source.AppendLine();

        if (model.Namespace is not null)
        {
            source.Append("namespace ").Append(model.Namespace).AppendLine(";");
            source.AppendLine();
        }

        foreach (string container in model.Containers)
        {
            source.Append("partial ").Append(container).AppendLine();
            source.AppendLine("{");
        }

        source.Append("partial ").Append(model.Keyword).Append(' ').Append(model.TypeName)
              .Append(" : IPdfFormSchema<").Append(model.TypeName).AppendLine(">");
        source.AppendLine("{");

        source.AppendLine("    /// <summary>The form fields this type maps to.</summary>");
        source.AppendLine("    public static IReadOnlyList<string> FieldNames { get; } =");
        source.AppendLine("    [");
        foreach (FieldModel field in mapped)
            source.Append("        \"").Append(Escape(field.FieldName)).AppendLine("\",");

        source.AppendLine("    ];");
        source.AppendLine();

        source.AppendLine("    /// <summary>Writes this type's values into a form.</summary>");
        source.AppendLine("    /// <param name=\"form\">The form to fill in.</param>");
        source.AppendLine("    /// <param name=\"value\">The instance to read from.</param>");
        source.Append("    public static void Fill(PdfForm form, ").Append(model.TypeName).AppendLine(" value)");
        source.AppendLine("    {");
        foreach (FieldModel field in mapped)
            EmitFill(source, field);

        source.AppendLine("    }");
        source.AppendLine();

        source.AppendLine("    /// <summary>Reads an instance of this type out of a form.</summary>");
        source.AppendLine("    /// <param name=\"form\">The form to read.</param>");
        source.Append("    public static ").Append(model.TypeName).AppendLine(" Read(PdfForm form)");
        source.AppendLine("    {");
        foreach (FieldModel field in mapped)
            EmitRead(source, field);

        EmitConstruction(source, model, mapped);
        source.AppendLine("    }");
        source.AppendLine("}");

        for (int i = 0; i < model.Containers.Length; i++)
            source.AppendLine("}");

        string hint = model.Containers.Length == 0
            ? model.TypeName
            : string.Join(".", model.Containers.Select(container => container.Split(' ').Last())) + "." + model.TypeName;

        production.AddSource($"{hint}.PdfForm.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void EmitFill(StringBuilder source, FieldModel field)
    {
        string access = "value." + field.MemberName;
        string name = "\"" + Escape(field.FieldName) + "\"";

        if (field.Kind == ValueKind.Boolean)
        {
            source.Append("        form.Set(").Append(name).Append(", ").Append(access)
                  .AppendLine(field.Nullable ? " ?? false);" : ");");
            return;
        }

        string formatted = FormatExpression(field, access);

        if (field.Nullable && field.Kind != ValueKind.String)
        {
            source.Append("        form.Set(").Append(name).Append(", ").Append(access)
                  .Append(" is { } ").Append(Local(field)).Append(" ? ")
                  .Append(FormatExpression(field, Local(field))).AppendLine(" : null);");
            return;
        }

        source.Append("        form.Set(").Append(name).Append(", ").Append(formatted).AppendLine(");");
    }

    private static string FormatExpression(FieldModel field, string access) => field.Kind switch
    {
        ValueKind.String => field.Nullable ? access : access,
        ValueKind.Enum => access + ".ToString()",
        ValueKind.DateTime or ValueKind.DateOnly or ValueKind.DateTimeOffset when field.Format is not null =>
            access + ".ToString(\"" + Escape(field.Format!) + "\", CultureInfo.InvariantCulture)",
        ValueKind.DateTime or ValueKind.DateTimeOffset => access + ".ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture)",
        ValueKind.DateOnly => access + ".ToString(\"yyyy-MM-dd\", CultureInfo.InvariantCulture)",
        ValueKind.Guid => access + ".ToString()",
        _ when field.Format is not null =>
            access + ".ToString(\"" + Escape(field.Format!) + "\", CultureInfo.InvariantCulture)",
        _ => access + ".ToString(CultureInfo.InvariantCulture)",
    };

    private static void EmitRead(StringBuilder source, FieldModel field)
    {
        string local = Local(field);
        string name = "\"" + Escape(field.FieldName) + "\"";

        source.Append("        string? ").Append(local).Append("Text = form[").Append(name).AppendLine("]?.Value;");

        switch (field.Kind)
        {
            case ValueKind.String:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ")
                      .Append(local).Append(" = ").Append(local).AppendLine(field.Nullable ? "Text;" : "Text ?? string.Empty;");
                break;

            case ValueKind.Boolean:
                source.Append("        bool").Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = ").Append(local).AppendLine("Text is { Length: > 0 } and not \"Off\" and not \"false\";");
                break;

            case ValueKind.Enum:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = Enum.TryParse<").Append(field.TypeName).Append(">(").Append(local)
                      .Append("Text, true, out var ").Append(local).Append("Parsed) ? ").Append(local)
                      .AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;

            case ValueKind.DateTime:
            case ValueKind.DateOnly:
            case ValueKind.DateTimeOffset:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = ").Append(field.TypeName).Append(".TryParse(").Append(local)
                      .Append("Text, CultureInfo.InvariantCulture, out var ").Append(local).Append("Parsed) ? ")
                      .Append(local).AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;

            case ValueKind.Guid:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = Guid.TryParse(").Append(local).Append("Text, out var ").Append(local)
                      .Append("Parsed) ? ").Append(local).AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;

            default:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = ").Append(field.TypeName).Append(".TryParse(").Append(local)
                      .Append("Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var ").Append(local)
                      .Append("Parsed) ? ").Append(local).AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;
        }
    }

    private static void EmitConstruction(StringBuilder source, FormModel model, ImmutableArray<FieldModel> mapped)
    {
        ImmutableArray<FieldModel> positional = mapped
            .Where(field => field.ConstructorOrdinal >= 0)
            .OrderBy(field => field.ConstructorOrdinal)
            .ToImmutableArray();

        if (positional.Length > 0)
        {
            source.Append("        return new ").Append(model.TypeName).Append('(')
                  .Append(string.Join(", ", positional.Select(Local)))
                  .AppendLine(")");

            ImmutableArray<FieldModel> rest = mapped
                .Where(field => field.ConstructorOrdinal < 0 && field.HasSetter)
                .ToImmutableArray();

            if (rest.Length == 0)
            {
                source.AppendLine("        {");
                source.AppendLine("        };");
                return;
            }

            source.AppendLine("        {");
            foreach (FieldModel field in rest)
                source.Append("            ").Append(field.MemberName).Append(" = ").Append(Local(field)).AppendLine(",");

            source.AppendLine("        };");
            return;
        }

        source.Append("        return new ").Append(model.TypeName).AppendLine();
        source.AppendLine("        {");
        foreach (FieldModel field in mapped.Where(field => field.HasSetter))
            source.Append("            ").Append(field.MemberName).Append(" = ").Append(Local(field)).AppendLine(",");

        source.AppendLine("        };");
    }

    private static string Local(FieldModel field) =>
        "_" + char.ToLowerInvariant(field.MemberName[0]) + field.MemberName.Substring(1);

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private enum ValueKind
    {
        Unsupported,
        String,
        Boolean,
        Integer,
        Number,
        Enum,
        DateTime,
        DateOnly,
        DateTimeOffset,
        Guid,
    }

    private sealed record FieldModel(
        string MemberName,
        string FieldName,
        string? Format,
        ValueKind Kind,
        bool Nullable,
        string TypeName,
        bool HasSetter,
        int ConstructorOrdinal);

    private sealed record FormModel(
        string? Namespace,
        string TypeName,
        string Keyword,
        string DisplayName,
        bool IsPartial,
        bool CanConstruct,
        ImmutableArray<FieldModel> Fields,
        ImmutableArray<string> Containers,
        Location Location);
}
