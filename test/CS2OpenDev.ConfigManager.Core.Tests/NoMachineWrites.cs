using System.Runtime.CompilerServices;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     Turns on the engine's refusal to write Windows or driver settings as soon as the test assembly loads, so no
///     test can change this PC's registry, power plan or driver, whatever it runs. Linked into every test assembly
///     that can reach the engine.
/// </summary>
internal static class NoMachineWrites
{
    [ModuleInitializer]
    internal static void Refuse() => ConfigBackup.MachineWritesRefused = true;
}
