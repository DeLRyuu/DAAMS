namespace ControlCenter.Models;

/// <summary>
/// One contributing factor in a risk score, as produced by the
/// Monitoring/Risk Assessment Engine (e.g. "+35 Classification: Confidential").
/// The Control Center only displays these; it never computes them.
/// </summary>
public class RiskFactor
{
    public required string Description { get; init; }
    public int Weight { get; init; }
}
