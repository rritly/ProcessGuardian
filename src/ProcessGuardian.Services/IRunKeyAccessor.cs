namespace ProcessGuardian.Services
{
    public interface IRunKeyAccessor
    {
        string? GetValue(string name);
        void SetValue(string name, string value);
        void DeleteValue(string name);
    }
}
