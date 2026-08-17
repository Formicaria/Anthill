using System.Runtime.CompilerServices;

namespace Anthill.Tests.Homelab;

/// <summary>
/// The same silence as <c>Anthill.Tests.TestConsole</c>, for this assembly. v0.3.8.78.
///
/// A module initializer is PER ASSEMBLY, so silencing one test project leaves the others narrating —
/// and a run whose noise depends on which assembly a test happens to live in is the confusing half
/// of the problem rather than the fix. Set <c>ANTHILL_TEST_CONSOLE=1</c> to restore it everywhere.
/// </summary>
internal static class TestConsole
{
    [ModuleInitializer]
    internal static void Silence()
    {
        if (Environment.GetEnvironmentVariable("ANTHILL_TEST_CONSOLE") is "1" or "true") return;
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
}
