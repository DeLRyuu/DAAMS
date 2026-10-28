using System;
using System.Collections.Generic;
using System.Linq;
using ControlCenter.Models;

namespace ControlCenter.Services;

public static class DeviceService
{
    public static List<DeviceSummary> Build(IEnumerable<ActivityLog> activity, IEnumerable<CaseItem> cases)
    {
        var actList = activity?.ToList() ?? new List<ActivityLog>();
        var caseList = cases?.ToList() ?? new List<CaseItem>();

        DateTime now = DateTime.Now;
        DateTime sevenDaysAgo = now.AddDays(-7);
        DateTime twentyFourHoursAgo = now.AddHours(-24);

        var deviceNames = actList.Select(a => a.Device)
            .Concat(caseList.Select(c => c.Device))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var summaries = new List<DeviceSummary>();

        foreach (var devName in deviceNames)
        {
            var devAct = actList.Where(a => string.Equals(a.Device, devName, StringComparison.OrdinalIgnoreCase)).ToList();
            var devCases = caseList.Where(c => string.Equals(c.Device, devName, StringComparison.OrdinalIgnoreCase)).ToList();

            var accounts = devAct.Select(a => a.User)
                .Concat(devCases.Select(c => c.User))
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u)
                .ToList();

            DateTime firstSeen = devAct.Count > 0
                ? devAct.Min(a => a.Timestamp)
                : (devCases.Count > 0 ? devCases.Min(c => c.DetectedAt) : now);

            DateTime lastActivity = devAct.Count > 0
                ? devAct.Max(a => a.Timestamp)
                : (devCases.Count > 0 ? devCases.Max(c => c.DetectedAt) : now);

            int events24h = devAct.Count(a => a.Timestamp >= twentyFourHoursAgo);
            int events7d = devAct.Count(a => a.Timestamp >= sevenDaysAgo);
            int openCases = devCases.Count(c => c.Group != "Closed");

            var last7dAct = devAct.Where(a => a.Timestamp >= sevenDaysAgo).ToList();

            RiskLevel? highestRisk = null;
            if (last7dAct.Count > 0)
            {
                var maxRiskItem = last7dAct.OrderByDescending(a => a.RiskScore ?? 0).FirstOrDefault();
                highestRisk = maxRiskItem?.RiskLevel;
            }

            var topAssets = last7dAct
                .Where(a => !string.IsNullOrWhiteSpace(a.Asset))
                .GroupBy(a => a.Asset, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DeviceAssetCount(g.Key, g.Count()))
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList();

            int[] hourCounts = new int[24];
            foreach (var a in last7dAct)
            {
                int h = a.Timestamp.Hour;
                if (h >= 0 && h < 24)
                {
                    hourCounts[h]++;
                }
            }

            int maxHour = hourCounts.Max();
            var hourBars = new List<DeviceHourBar>(24);
            for (int h = 0; h < 24; h++)
            {
                int count = hourCounts[h];
                double heightPx = 4;
                if (maxHour > 0 && count > 0)
                {
                    heightPx = 4.0 + ((double)count / maxHour) * 44.0;
                }
                bool isWorkHour = (h >= 7 && h <= 18);
                hourBars.Add(new DeviceHourBar(h, heightPx, isWorkHour));
            }

            summaries.Add(new DeviceSummary
            {
                Name = devName,
                Accounts = accounts,
                FirstSeen = firstSeen,
                LastActivity = lastActivity,
                Events24h = events24h,
                Events7d = events7d,
                OpenCases = openCases,
                HighestRisk = highestRisk,
                TopAssets = topAssets,
                HourCounts = hourCounts,
                HourBars = hourBars
            });
        }

        return summaries;
    }
}
