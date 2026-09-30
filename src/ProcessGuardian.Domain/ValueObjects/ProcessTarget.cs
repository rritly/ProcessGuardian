namespace ProcessGuardian.Domain.ValueObjects;

public sealed record ProcessTarget(
    string FullPath,
    string NameWithoutExtension,
    string WorkingDirectory
);
