using System;
using System.Collections.Generic;
using System.Linq;
using ControlCenter.Models;

namespace ControlCenter.Services;

public static class ActivityCollapser
{
    public static List<ActivityLog> Collapse(IEnumerable<ActivityLog> logs, int windowSeconds = 60)
    {
        var sorted = logs.OrderByDescending(a => a.Timestamp).ToList();
        var collapsed = new List<ActivityLog>();

        ActivityLog? currentAnchor = null;
        var currentTimestamps = new List<DateTime>();
        int currentRepeatCount = 1;
        ActivityLog? topRisk = null;

        foreach (var item in sorted)
        {
            if (currentAnchor == null)
            {
                currentAnchor = item;
                currentTimestamps = new List<DateTime> { item.Timestamp };
                currentRepeatCount = 1;
                topRisk = item;
                continue;
            }

            bool isSameGroup = string.Equals(currentAnchor.User, item.User, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(currentAnchor.Device, item.Device, StringComparison.OrdinalIgnoreCase)
                               && string.Equals(currentAnchor.Asset, item.Asset, StringComparison.OrdinalIgnoreCase)
                               && currentAnchor.Action == item.Action
                               && (currentAnchor.Timestamp - item.Timestamp) <= TimeSpan.FromSeconds(windowSeconds)
                               && (currentAnchor.Timestamp - item.Timestamp) >= TimeSpan.Zero;

            if (isSameGroup)
            {
                currentRepeatCount++;
                currentTimestamps.Add(item.Timestamp);
                if ((item.RiskScore ?? -1) > (topRisk?.RiskScore ?? -1))
                {
                    topRisk = item;
                }
                if (string.IsNullOrEmpty(currentAnchor.MatchedAlertId) && !string.IsNullOrEmpty(item.MatchedAlertId))
                {
                    currentAnchor.MatchedAlertId = item.MatchedAlertId;
                }
            }
            else
            {
                MergeAnchor(currentAnchor, topRisk, currentRepeatCount, currentTimestamps);
                collapsed.Add(currentAnchor);

                currentAnchor = item;
                currentTimestamps = new List<DateTime> { item.Timestamp };
                currentRepeatCount = 1;
                topRisk = item;
            }
        }

        if (currentAnchor != null)
        {
            MergeAnchor(currentAnchor, topRisk, currentRepeatCount, currentTimestamps);
            collapsed.Add(currentAnchor);
        }

        return collapsed;
    }

    private static void MergeAnchor(ActivityLog anchor, ActivityLog? topRisk, int repeatCount, List<DateTime> timestamps)
    {
        var highestRisk = topRisk ?? anchor;
        typeof(ActivityLog).GetProperty(nameof(ActivityLog.RiskScore))?.SetValue(anchor, highestRisk.RiskScore);
        typeof(ActivityLog).GetProperty(nameof(ActivityLog.RiskLevel))?.SetValue(anchor, highestRisk.RiskLevel);
        anchor.RepeatCount = repeatCount;
        anchor.MergedTimestamps = timestamps;
    }
}
