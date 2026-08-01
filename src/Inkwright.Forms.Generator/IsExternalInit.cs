using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// The marker the compiler needs for init-only setters. It ships with .NET 5 and later; a Roslyn
/// component targets netstandard2.0, so it has to declare the type itself.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
