namespace Carbonado.UI.Tests.TestSupport;

/// <summary>
/// Tests that change the process-wide <see cref="Carbonado.UI.Motion.MotionSettings"/> (or assert motion those changes
/// would alter) join this collection: they run one at a time, after the parallel tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MotionSettingsCollection
{
    public const string Name = "MotionSettings";
}
