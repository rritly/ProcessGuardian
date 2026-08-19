using System;
using System.IO;

namespace ProcessGuardian.Core
{
    public static class AppSettingsValidator
    {
        public static ValidationResult Validate(AppSettings s)
        {
            var r = new ValidationResult { IsValid = true };

            if (s == null)
            {
                r.IsValid = false;
                r.Errors.Add("Settings object is null");
                return r;
            }

            // SchemaVersion must be 1 for now
            if (s.SchemaVersion != 1)
            {
                r.IsValid = false;
                r.Errors.Add($"Unsupported SchemaVersion: {s.SchemaVersion}");
                return r;
            }

            // Monitoring enabled requires a path
            if (s.MonitoringEnabled)
            {
                if (string.IsNullOrWhiteSpace(s.TargetProcessPath))
                {
                    r.IsValid = false;
                    r.Errors.Add("TargetProcessPath must be set when monitoring is enabled.");
                }
            }

            // If path provided, must be absolute and end with .exe
            if (!string.IsNullOrWhiteSpace(s.TargetProcessPath))
            {
                try
                {
                    if (!Path.IsPathRooted(s.TargetProcessPath))
                    {
                        r.IsValid = false;
                        r.Errors.Add("TargetProcessPath must be an absolute path.");
                    }
                    else if (!s.TargetProcessPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        r.IsValid = false;
                        r.Errors.Add("TargetProcessPath must point to an .exe file.");
                    }
                }
                catch (Exception ex)
                {
                    r.IsValid = false;
                    r.Errors.Add($"TargetProcessPath is invalid: {ex.Message}");
                }
            }

            // Numeric ranges - basic checks per TZ
            if (s.InitialDelaySeconds < 0)
            {
                r.IsValid = false;
                r.Errors.Add("InitialDelaySeconds must be >= 0.");
            }
            if (s.CheckIntervalSeconds <= 0)
            {
                r.IsValid = false;
                r.Errors.Add("CheckIntervalSeconds must be > 0.");
            }
            if (s.MaxRestartAttempts < 0)
            {
                r.IsValid = false;
                r.Errors.Add("MaxRestartAttempts must be >= 0.");
            }
            if (s.RestartDelaySeconds < 0)
            {
                r.IsValid = false;
                r.Errors.Add("RestartDelaySeconds must be >= 0.");
            }
            if (s.StartupVerificationTimeoutSeconds <= 0)
            {
                r.IsValid = false;
                r.Errors.Add("StartupVerificationTimeoutSeconds must be > 0.");
            }
            if (s.FailureCooldownSeconds < 0)
            {
                r.IsValid = false;
                r.Errors.Add("FailureCooldownSeconds must be >= 0.");
            }
            if (s.LogBufferSize <= 0)
            {
                r.IsValid = false;
                r.Errors.Add("LogBufferSize must be > 0.");
            }

            return r;
        }
    }
}
