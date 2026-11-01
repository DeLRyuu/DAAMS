using System.Linq;

namespace ControlCenter.Models;

public static class EngineDefaults
{
    public const int WorkStartHour = 7, WorkEndHour = 19;
    public const int OffHoursPenalty = 25, NearEdgePenalty = 8, BurstThreshold = 3;
    public static readonly (string Name, int Weight)[] Classifications =
        { ("Public", 5), ("Sensitive", 15), ("Private", 25), ("Confidential", 35) };
    public static readonly (string Name, int Weight)[] Actions =
        { ("Open", 2), ("Modify", 8), ("Rename", 6), ("Copy", 18), ("Move", 14), ("Delete", 22) };
    public static string ClassificationText => string.Join(", ", Classifications.Select(c => $"{c.Name} {c.Weight}"));
            public static string ActionText => string.Join(", ", Actions.Select(a => $"{a.Name} +{a.Weight}"));
}
