namespace WebContent
{
    /// <summary>
    /// Compile-time stand-in for the auto-generated <c>WebContent.Strings</c> resource
    /// class. The test project links pure production files (<c>WebContentConfiguration.cs</c>,
    /// <c>WorkspaceConfiguration.cs</c>) that reference <c>Strings.HomeAddress</c>, but it does
    /// not embed the .resx. Tests never read this value — they always pass an explicit
    /// fallback address — so it only exists to satisfy the compiler.
    /// </summary>
    internal static class Strings
    {
        public const string HomeAddress = "https://www.cascadia.tech/";
    }
}
