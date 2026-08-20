using ProcessGuardian.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Tests.Fakes
{
    internal class FakeTimeProvider : ITimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
        private readonly List<Scheduled> _scheduled = new();

        private class Scheduled
        {
            public DateTimeOffset WakeAt;
            public TaskCompletionSource<object?> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration? Reg;
        }

        public DateTimeOffset UtcNow => _utcNow;

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero)
                return Task.CompletedTask;

            var s = new Scheduled { WakeAt = _utcNow + delay };
            if (ct.CanBeCanceled)
            {
                s.Reg = ct.Register(() => s.Tcs.TrySetCanceled(ct));
            }
            lock (_scheduled)
            {
                _scheduled.Add(s);
            }
            return s.Tcs.Task;
        }

        public void AdvanceBy(TimeSpan delta)
        {
            if (delta <= TimeSpan.Zero)
                return;
            _utcNow = _utcNow.Add(delta);
            List<Scheduled> toComplete;
            lock (_scheduled)
            {
                toComplete = _scheduled.Where(x => x.WakeAt <= _utcNow).ToList();
                foreach (var s in toComplete) _scheduled.Remove(s);
            }

            foreach (var s in toComplete)
            {
                s.Reg?.Dispose();
                s.Tcs.TrySetResult(null);
            }
        }

        // Helper for tests: advance until no scheduled remain
        public void CompleteAll()
        {
            List<Scheduled> all;
            lock (_scheduled)
            {
                all = _scheduled.ToList();
                _scheduled.Clear();
            }
            foreach (var s in all)
            {
                s.Reg?.Dispose();
                s.Tcs.TrySetResult(null);
            }
        }
    }
}
