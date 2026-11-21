namespace Carbonado.Core.Encoders;

/// <summary>How much CPU to spend for density. Each adapter maps it to its own effort/speed settings.</summary>
public enum EffortLevel
{
    Fast,
    Balanced,
    Max,
    Insane,
}
