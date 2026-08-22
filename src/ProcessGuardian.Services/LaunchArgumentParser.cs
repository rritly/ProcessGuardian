namespace ProcessGuardian.Services
{
    public static class LaunchArgumentParser
    {
        public static bool IsBackgroundLaunch(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return false;

            // Simple whitespace tokenization; trim quotes from tokens
            var tokens = arguments.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in tokens)
            {
                var token = t.Trim();
                if (token.StartsWith("\"") && token.EndsWith("\"") && token.Length >= 2)
                {
                    token = token.Substring(1, token.Length - 2);
                }

                if (string.Equals(token, "--background", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
