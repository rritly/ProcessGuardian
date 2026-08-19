namespace ProcessGuardian.Core
{
    public enum GuardianStatus
    {
        Stopped,
        WaitingInitialDelay,
        Monitoring,
        Restarting,
        Cooldown,
        Error
    }
}
