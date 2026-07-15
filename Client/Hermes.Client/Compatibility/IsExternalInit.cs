// Compatibility shim for C# record/init support when targeting netstandard2.1.
// The type is only required by the compiler and is embedded in Hermes.Client.dll.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
