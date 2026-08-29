using System.Runtime.CompilerServices;

namespace Mailbox.Tests;

/// <summary>
/// Registers the legacy code pages before any test in this assembly runs.
/// </summary>
/// <remarks>
/// The same reason the product does it at its composition root, and the reason it has to happen
/// *first*: MimeKit caches charset lookups statically, and a name it failed to resolve stays
/// failed for the life of the process. A single MIME parse before the provider is registered
/// therefore poisons every later one, and which test parses first is decided by the order the
/// runner happens to pick — which is why four code pages passed locally and failed on CI at the
/// same commit.
/// <para>
/// <c>Mailbox.Protocols</c> carries an initializer of its own for the same reason. This one covers
/// the tests that reach MimeKit without going through it.
/// </para>
/// </remarks>
internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void RegisterCodePages() => Mailbox.Protocols.LegacyCodePages.Register();
}
