using System.Collections.Generic;

namespace ProcessGuardian.Core
{
    public sealed class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; } = new List<string>();

        public static ValidationResult Valid() => new ValidationResult { IsValid = true };
        public static ValidationResult Invalid(params string[] errors)
        {
            var r = new ValidationResult { IsValid = false };
            r.Errors.AddRange(errors);
            return r;
        }
    }
}
