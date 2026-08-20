using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace ProcessGuardian.Core
{
    public enum ProcessInspectionOutcome
    {
        TargetFound,
        TargetNotFound,
        SameNameDifferentPath,
        ProcessInformationUnavailable
    }

    public static class ProcessIdentity
    {
        public static ProcessInspectionOutcome Inspect(IEnumerable<ProcessInfo> candidates, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                return ProcessInspectionOutcome.TargetNotFound;

            var normalizedTarget = NormalizePath(targetPath);

            var list = candidates.ToList();

            bool anySameName = list.Count > 0;
            bool anyInfoUnavailable = list.Any(p => !p.IsPathAvailable);

            foreach (var p in list)
            {
                if (p.IsPathAvailable && !string.IsNullOrWhiteSpace(p.ExecutablePath))
                {
                    var normalized = NormalizePath(p.ExecutablePath!);
                    if (string.Equals(normalized, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                        return ProcessInspectionOutcome.TargetFound;
                }
            }

            if (anyInfoUnavailable)
                return ProcessInspectionOutcome.ProcessInformationUnavailable;

            if (anySameName)
                return ProcessInspectionOutcome.SameNameDifferentPath;

            return ProcessInspectionOutcome.TargetNotFound;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return full;
            }
            catch
            {
                // If normalization fails, fall back to trimmed input
                return path.Trim();
            }
        }
    }
}
