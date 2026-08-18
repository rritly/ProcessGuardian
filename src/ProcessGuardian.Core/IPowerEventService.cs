using System;
using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface IPowerEventService
    {
        event EventHandler? Resume;
        event EventHandler? Suspend;
    }
}
