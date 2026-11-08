using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        "A form type and its containing types must be partial",
        "'{0}' is marked with [PdfForm] but it or one of its containing types is not declared partial, so the mapping cannot be generated",
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

    private static readonly DiagnosticDescriptor UnsupportedFormType = new(
        "IWF003",
        "A form type cannot be generated",
        "'{0}' cannot be used as a form schema: {1}",
        "Inkwright",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CannotConstructForm = new(
        "IWF004",
        "A form type cannot be read",
        "'{0}' cannot be read from a form: {1}",
        "Inkwright",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly SymbolDisplayFormat TypeDisplayFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

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

        var compilation = (CSharpCompilation)syntax.SemanticModel.Compilation;
        bool partial = IsPartial(type);

        var members = ImmutableArray.CreateBuilder<FieldModel>();
        var memberSymbols = ImmutableArray.CreateBuilder<IPropertySymbol>();
        var ignoredMemberSymbols = ImmutableArray.CreateBuilder<IPropertySymbol>();
        IMethodSymbol? propertyConstructor = FindPropertyConstructor(type, compilation);

        foreach (IPropertySymbol property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public)
                continue;

            if (property.GetMethod is null)
                continue;

            IParameterSymbol? parameter = propertyConstructor?.Parameters
                .FirstOrDefault(p => string.Equals(p.Name, property.Name, System.StringComparison.OrdinalIgnoreCase));

            if (HasAttribute(property, IgnoreAttribute) ||
                parameter is not null && HasAttribute(parameter, IgnoreAttribute))
            {
                ignoredMemberSymbols.Add(property);
                continue;
            }

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
                property.SetMethod is not null,
                property.IsRequired,
                -1,
                "_field" + members.Count));
            memberSymbols.Add(property);
        }

        // A form type is often nested inside the class that uses it, so the generated half has to be
        // wrapped in the same chain of partial declarations.
        var containers = ImmutableArray.CreateBuilder<TypeDeclarationModel>();
        for (INamedTypeSymbol? container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            containers.Insert(0, new TypeDeclarationModel(DeclarationKeyword(container), DeclarationName(container)));
            partial &= IsPartial(container);
        }

        ImmutableArray<FieldModel> fields = members.ToImmutable();
        ConstructorSelection construction = SelectConstructor(
            type,
            fields,
            memberSymbols.ToImmutable(),
            ignoredMemberSymbols.ToImmutable(),
            compilation);
        if (construction.FailureReason is null)
        {
            var constructedFields = fields.ToBuilder();
            for (int ordinal = 0; ordinal < construction.Arguments.Length; ordinal++)
            {
                int fieldIndex = construction.Arguments[ordinal].FieldIndex;
                if (fieldIndex < 0)
                    continue;

                constructedFields[fieldIndex] = constructedFields[fieldIndex] with { ConstructorOrdinal = ordinal };
            }

            fields = constructedFields.ToImmutable();
        }

        return new FormModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : NamespaceName(type.ContainingNamespace),
            DeclarationName(type),
            DeclarationKeyword(type),
            type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            partial,
            UnsupportedShapeReason(type),
            construction.FailureReason,
            construction.Arguments,
            construction.SetsRequiredMembers,
            fields,
            containers.ToImmutable(),
            declaration.Identifier.GetLocation(),
            HintName(type));
    }

    private static bool IsPartial(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.All(reference =>
        reference.GetSyntax() is TypeDeclarationSyntax declaration &&
        declaration.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PartialKeyword)));

    private static IMethodSymbol? FindPropertyConstructor(INamedTypeSymbol type, CSharpCompilation compilation)
    {
        IEnumerable<IPropertySymbol> properties = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public);

        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.Length == 0)
                continue;

            bool matches = constructor.Parameters.All(parameter =>
                properties.Any(property =>
                    string.Equals(property.Name, parameter.Name, System.StringComparison.OrdinalIgnoreCase) &&
                    compilation.ClassifyConversion(property.Type, parameter.Type).IsImplicit));

            if (matches)
                return constructor;
        }

        return null;
    }

    private static ConstructorSelection SelectConstructor(
        INamedTypeSymbol type,
        ImmutableArray<FieldModel> fields,
        ImmutableArray<IPropertySymbol> properties,
        ImmutableArray<IPropertySymbol> ignoredProperties,
        CSharpCompilation compilation)
    {
        ImmutableArray<string> requiredMembers = RequiredMembers(type);
        var candidates = new List<ConstructorSelection>();

        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.Any(parameter => parameter.RefKind != RefKind.None))
                continue;

            var arguments = ImmutableArray.CreateBuilder<ConstructorArgumentModel>(constructor.Parameters.Length);
            var used = new HashSet<int>();
            bool usable = true;

            foreach (IParameterSymbol parameter in constructor.Parameters)
            {
                int index = FindField(fields, parameter.Name, used);
                if (index >= 0 &&
                    fields[index].Kind != ValueKind.Unsupported &&
                    compilation.ClassifyConversion(properties[index].Type, parameter.Type).IsImplicit)
                {
                    arguments.Add(new ConstructorArgumentModel(parameter.Name, index, fields[index].LocalName));
                    used.Add(index);
                    continue;
                }

                IPropertySymbol? ignored = FindProperty(ignoredProperties, parameter.Name);
                if (ignored is not null && compilation.ClassifyConversion(ignored.Type, parameter.Type).IsImplicit)
                {
                    // A positional-record member can be deliberately left out of the schema. Read
                    // still has to call its primary constructor, so supply the type's default value
                    // without ever reading or writing a form field for the ignored member.
                    arguments.Add(new ConstructorArgumentModel(parameter.Name, -1, LocalName: null));
                    continue;
                }

                if (parameter.IsOptional || parameter.IsParams)
                    continue;

                usable = false;
                break;
            }

            if (!usable)
                continue;

            bool setsRequiredMembers = HasAttribute(
                constructor,
                "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

            if (!CanAssignAllFields(fields, used) ||
                (!setsRequiredMembers && !CanAssignRequiredMembers(fields, requiredMembers)))
            {
                continue;
            }

            candidates.Add(new ConstructorSelection(
                setsRequiredMembers,
                arguments.ToImmutable(),
                FailureReason: null));
        }

        // Metadata does not always expose the implicit parameterless value-type constructor.
        if (type.IsValueType && candidates.All(candidate => candidate.Arguments.Length != 0))
        {
            var used = new HashSet<int>();
            if (CanAssignAllFields(fields, used) && CanAssignRequiredMembers(fields, requiredMembers))
            {
                candidates.Add(new ConstructorSelection(
                    SetsRequiredMembers: false,
                    Arguments: ImmutableArray<ConstructorArgumentModel>.Empty,
                    FailureReason: null));
            }
        }

        if (candidates.Count > 0)
        {
            return candidates
                .OrderBy(candidate => candidate.Arguments.Length)
                .First();
        }

        string[] unassignableRequired = requiredMembers
            .Where(name => !fields.Any(field =>
                field.MemberName == name && field.Kind != ValueKind.Unsupported && field.HasSetter))
            .ToArray();

        if (unassignableRequired.Length > 0)
        {
            return ConstructorSelection.Failed(
                "required member(s) " + string.Join(", ", unassignableRequired.Select(Literal)) +
                " are ignored, unsupported, or do not have a setter");
        }

        string[] readOnly = fields
            .Where(field => field.Kind != ValueKind.Unsupported && !field.HasSetter)
            .Select(field => field.MemberName)
            .ToArray();

        if (readOnly.Length > 0)
        {
            return ConstructorSelection.Failed(
                "read-only member(s) " + string.Join(", ", readOnly.Select(Literal)) +
                " are not covered by a usable instance constructor");
        }

        return ConstructorSelection.Failed(
            "no instance constructor can be called using only supported mapped members");
    }

    private static int FindField(
        ImmutableArray<FieldModel> fields,
        string parameterName,
        HashSet<int> used)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (!used.Contains(i) && fields[i].MemberName == parameterName)
                return i;
        }

        for (int i = 0; i < fields.Length; i++)
        {
            if (!used.Contains(i) && string.Equals(
                    fields[i].MemberName,
                    parameterName,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static IPropertySymbol? FindProperty(
        ImmutableArray<IPropertySymbol> properties,
        string parameterName)
    {
        IPropertySymbol? insensitive = null;
        foreach (IPropertySymbol property in properties)
        {
            if (property.Name == parameterName)
                return property;

            if (insensitive is null && string.Equals(
                    property.Name,
                    parameterName,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                insensitive = property;
            }
        }

        return insensitive;
    }

    private static bool CanAssignAllFields(ImmutableArray<FieldModel> fields, HashSet<int> constructorFields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Kind != ValueKind.Unsupported && !fields[i].HasSetter && !constructorFields.Contains(i))
                return false;
        }

        return true;
    }

    private static bool CanAssignRequiredMembers(
        ImmutableArray<FieldModel> fields,
        ImmutableArray<string> requiredMembers) => requiredMembers.All(name => fields.Any(field =>
            field.MemberName == name && field.Kind != ValueKind.Unsupported && field.HasSetter));

    private static ImmutableArray<string> RequiredMembers(INamedTypeSymbol type)
    {
        var names = ImmutableArray.CreateBuilder<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false, IsRequired: true } property)
                    names.Add(property.Name);
                else if (member is IFieldSymbol { IsStatic: false, IsRequired: true } field)
                    names.Add(field.Name);
            }
        }

        return names.ToImmutable();
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

    private static string DeclarationKeyword(INamedTypeSymbol type) => type.TypeKind == TypeKind.Interface
        ? "interface"
        : type.IsRecord
            ? type.IsValueType ? "record struct" : "record"
            : type.IsValueType ? "struct" : "class";

    private static string DeclarationName(INamedTypeSymbol type)
    {
        var name = new StringBuilder(EscapeIdentifier(type.Name));
        if (type.TypeParameters.Length == 0)
            return name.ToString();

        name.Append('<');
        for (int i = 0; i < type.TypeParameters.Length; i++)
        {
            if (i > 0)
                name.Append(", ");

            ITypeParameterSymbol parameter = type.TypeParameters[i];
            if (parameter.Variance == VarianceKind.In)
                name.Append("in ");
            else if (parameter.Variance == VarianceKind.Out)
                name.Append("out ");

            name.Append(EscapeIdentifier(parameter.Name));
        }

        return name.Append('>').ToString();
    }

    private static string NamespaceName(INamespaceSymbol value)
    {
        var parts = new Stack<string>();
        for (INamespaceSymbol? current = value; current is { IsGlobalNamespace: false }; current = current.ContainingNamespace)
            parts.Push(EscapeIdentifier(current.Name));

        return string.Join(".", parts);
    }

    private static string? UnsupportedShapeReason(INamedTypeSymbol type)
    {
        if (type.TypeKind is not TypeKind.Class and not TypeKind.Struct)
            return "only classes, structs, and records can be form schemas";
        if (type.IsStatic)
            return "static types cannot implement IPdfFormSchema<T> or be returned from Read";
        if (type.IsAbstract)
            return "abstract types cannot be created by Read";
        if (type.IsRefLikeType)
            return "ref-like types cannot implement IPdfFormSchema<T>";
        if (type.IsFileLocal || ContainingTypes(type).Any(container => container.IsFileLocal))
        {
            return "file-local types cannot be extended from a generated source file";
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> ContainingTypes(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type.ContainingType; current is not null; current = current.ContainingType)
            yield return current;
    }

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    private static string HintName(INamedTypeSymbol type)
    {
        var parts = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            parts.Push(current.MetadataName);

        string metadataName = type.ContainingNamespace.IsGlobalNamespace
            ? string.Join("+", parts)
            : type.ContainingNamespace.ToDisplayString() + "::" + string.Join("+", parts);

        // Encode every UTF-16 code unit. Leaving an underscore verbatim while spelling a plus as
        // `_002B_`, for example, makes the transform non-injective and lets two legal type names
        // crash the entire generator with duplicate AddSource hint names.
        var hint = new StringBuilder((metadataName.Length * 4) + 20).Append("IWF_");
        foreach (char character in metadataName)
            hint.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));

        return hint.Append(".PdfForm.g.cs").ToString();
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

        typeName = effective.ToDisplayString(TypeDisplayFormat);

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
            production.ReportDiagnostic(Diagnostic.Create(MustBePartial, model.Location, model.DisplayName));
            return;
        }

        foreach (FieldModel field in model.Fields)
        {
            if (field.Kind == ValueKind.Unsupported)
            {
                production.ReportDiagnostic(
                    Diagnostic.Create(UnsupportedType, model.Location, model.DisplayName, field.MemberName, field.TypeName));
            }
        }

        if (model.UnsupportedShapeReason is not null)
        {
            production.ReportDiagnostic(Diagnostic.Create(
                UnsupportedFormType,
                model.Location,
                model.DisplayName,
                model.UnsupportedShapeReason));
            return;
        }

        if (model.ConstructionFailureReason is not null)
        {
            production.ReportDiagnostic(Diagnostic.Create(
                CannotConstructForm,
                model.Location,
                model.DisplayName,
                model.ConstructionFailureReason));
            return;
        }

        ImmutableArray<FieldModel> mapped = model.Fields.Where(f => f.Kind != ValueKind.Unsupported).ToImmutableArray();

        var source = new StringBuilder(4096);
        source.AppendLine("// <auto-generated by Inkwright.Forms.Generator />");
        source.AppendLine("#nullable enable");
        source.AppendLine();

        if (model.Namespace is not null)
        {
            source.Append("namespace ").Append(model.Namespace).AppendLine(";");
            source.AppendLine();
        }

        foreach (TypeDeclarationModel container in model.Containers)
        {
            source.Append("partial ").Append(container.Keyword).Append(' ').Append(container.Name).AppendLine();
            source.AppendLine("{");
        }

        source.Append("partial ").Append(model.Keyword).Append(' ').Append(model.TypeName)
              .Append(" : global::Inkwright.Forms.IPdfFormSchema<").Append(model.TypeName).AppendLine(">");
        source.AppendLine("{");

        source.AppendLine("    /// <summary>The form fields this type maps to.</summary>");
        source.AppendLine("    public static global::System.Collections.Generic.IReadOnlyList<string> FieldNames { get; } =");
        source.AppendLine("    [");
        foreach (FieldModel field in mapped)
            source.Append("        ").Append(Literal(field.FieldName)).AppendLine(",");

        source.AppendLine("    ];");
        source.AppendLine();

        source.AppendLine("    /// <summary>Writes this type's values into a form.</summary>");
        source.AppendLine("    /// <param name=\"form\">The form to fill in.</param>");
        source.AppendLine("    /// <param name=\"value\">The instance to read from.</param>");
        source.Append("    public static void Fill(global::Inkwright.Forms.PdfForm form, ")
            .Append(model.TypeName).AppendLine(" value)");
        source.AppendLine("    {");
        foreach (FieldModel field in mapped)
            EmitFill(source, field);

        source.AppendLine("    }");
        source.AppendLine();

        source.AppendLine("    /// <summary>Reads an instance of this type out of a form.</summary>");
        source.AppendLine("    /// <param name=\"form\">The form to read.</param>");
        source.Append("    public static ").Append(model.TypeName)
            .AppendLine(" Read(global::Inkwright.Forms.PdfForm form)");
        source.AppendLine("    {");
        foreach (FieldModel field in mapped)
            EmitRead(source, field);

        EmitConstruction(source, model, mapped);
        source.AppendLine("    }");
        source.AppendLine("}");

        for (int i = 0; i < model.Containers.Length; i++)
            source.AppendLine("}");

        production.AddSource(model.HintName, SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void EmitFill(StringBuilder source, FieldModel field)
    {
        string access = "value." + EscapeIdentifier(field.MemberName);
        string name = Literal(field.FieldName);

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
            access + ".ToString(" + Literal(field.Format!) + ", global::System.Globalization.CultureInfo.InvariantCulture)",
        ValueKind.DateTime or ValueKind.DateTimeOffset =>
            access + ".ToString(\"yyyy-MM-dd\", global::System.Globalization.CultureInfo.InvariantCulture)",
        ValueKind.DateOnly =>
            access + ".ToString(\"yyyy-MM-dd\", global::System.Globalization.CultureInfo.InvariantCulture)",
        ValueKind.Guid => access + ".ToString()",
        _ when field.Format is not null =>
            access + ".ToString(" + Literal(field.Format!) + ", global::System.Globalization.CultureInfo.InvariantCulture)",
        _ => access + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)",
    };

    private static void EmitRead(StringBuilder source, FieldModel field)
    {
        string local = Local(field);
        string name = Literal(field.FieldName);

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
                      .Append(" = global::System.Enum.TryParse<").Append(field.TypeName).Append(">(").Append(local)
                      .Append("Text, true, out var ").Append(local).Append("Parsed) ? ").Append(local)
                      .AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;

            case ValueKind.DateTime:
            case ValueKind.DateOnly:
            case ValueKind.DateTimeOffset:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = ").Append(field.TypeName).Append(".TryParse(").Append(local)
                      .Append("Text, global::System.Globalization.CultureInfo.InvariantCulture, out var ")
                      .Append(local).Append("Parsed) ? ")
                      .Append(local).AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;

            case ValueKind.Guid:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = global::System.Guid.TryParse(").Append(local).Append("Text, out var ").Append(local)
                      .Append("Parsed) ? ").Append(local).AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;

            default:
                source.Append("        ").Append(field.TypeName).Append(field.Nullable ? "? " : " ").Append(local)
                      .Append(" = ").Append(field.TypeName).Append(".TryParse(").Append(local)
                      .Append("Text, global::System.Globalization.NumberStyles.Any, ")
                      .Append("global::System.Globalization.CultureInfo.InvariantCulture, out var ").Append(local)
                      .Append("Parsed) ? ").Append(local).AppendLine(field.Nullable ? "Parsed : null;" : "Parsed : default;");
                break;
        }
    }

    private static void EmitConstruction(StringBuilder source, FormModel model, ImmutableArray<FieldModel> mapped)
    {
        if (model.ConstructorArguments.Length > 0)
        {
            source.Append("        return new ").Append(model.TypeName).Append('(')
                  .Append(string.Join(", ", model.ConstructorArguments.Select(argument =>
                      EscapeIdentifier(argument.ParameterName) + ": " + (argument.LocalName ?? "default!"))))
                  .AppendLine(")");

            ImmutableArray<FieldModel> rest = mapped
                .Where(field => field.HasSetter &&
                    (field.ConstructorOrdinal < 0 || field.IsRequired && !model.ConstructorSetsRequiredMembers))
                .ToImmutableArray();

            if (rest.Length == 0)
            {
                source.AppendLine("        {");
                source.AppendLine("        };");
                return;
            }

            source.AppendLine("        {");
            foreach (FieldModel field in rest)
                source.Append("            ").Append(EscapeIdentifier(field.MemberName))
                    .Append(" = ").Append(Local(field)).AppendLine(",");

            source.AppendLine("        };");
            return;
        }

        source.Append("        return new ").Append(model.TypeName).AppendLine();
        source.AppendLine("        {");
        foreach (FieldModel field in mapped.Where(field => field.HasSetter))
            source.Append("            ").Append(EscapeIdentifier(field.MemberName))
                .Append(" = ").Append(Local(field)).AppendLine(",");

        source.AppendLine("        };");
    }

    private static string Local(FieldModel field) => field.LocalName;

    private static string Literal(string text)
    {
        var literal = new StringBuilder(text.Length + 2).Append('"');
        foreach (char character in text)
        {
            switch (character)
            {
                case '\\': literal.Append("\\\\"); break;
                case '"': literal.Append("\\\""); break;
                case '\0': literal.Append("\\0"); break;
                case '\a': literal.Append("\\a"); break;
                case '\b': literal.Append("\\b"); break;
                case '\f': literal.Append("\\f"); break;
                case '\n': literal.Append("\\n"); break;
                case '\r': literal.Append("\\r"); break;
                case '\t': literal.Append("\\t"); break;
                case '\v': literal.Append("\\v"); break;
                default:
                    if (char.IsControl(character) || char.IsSurrogate(character) || character is '\u2028' or '\u2029')
                    {
                        literal.Append("\\u")
                            .Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        literal.Append(character);
                    }

                    break;
            }
        }

        return literal.Append('"').ToString();
    }

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
        bool IsRequired,
        int ConstructorOrdinal,
        string LocalName);

    private sealed record TypeDeclarationModel(string Keyword, string Name);

    private sealed record ConstructorArgumentModel(
        string ParameterName,
        int FieldIndex,
        string? LocalName);

    private sealed record ConstructorSelection(
        bool SetsRequiredMembers,
        ImmutableArray<ConstructorArgumentModel> Arguments,
        string? FailureReason)
    {
        internal static ConstructorSelection Failed(string reason) => new(
            SetsRequiredMembers: false,
            Arguments: ImmutableArray<ConstructorArgumentModel>.Empty,
            FailureReason: reason);
    }

    private sealed record FormModel(
        string? Namespace,
        string TypeName,
        string Keyword,
        string DisplayName,
        bool IsPartial,
        string? UnsupportedShapeReason,
        string? ConstructionFailureReason,
        ImmutableArray<ConstructorArgumentModel> ConstructorArguments,
        bool ConstructorSetsRequiredMembers,
        ImmutableArray<FieldModel> Fields,
        ImmutableArray<TypeDeclarationModel> Containers,
        Location Location,
        string HintName);
}
