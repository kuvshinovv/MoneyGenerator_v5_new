using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public class ExponentialBackoff
    {
        private readonly TimeSpan _minDelay;
        private readonly TimeSpan _maxDelay;
        private int _attemptCount;

        public ExponentialBackoff(TimeSpan minDelay, TimeSpan maxDelay)
        {
            _minDelay = minDelay;
            _maxDelay = maxDelay;
        }

        public TimeSpan GetNextDelay()
        {
            _attemptCount++;
            var delay = TimeSpan.FromSeconds(Math.Pow(2, _attemptCount));
            return TimeSpan.FromTicks(Math.Min(delay.Ticks, _maxDelay.Ticks));
        }

        public void Reset()
        {
            _attemptCount = 0;
        }
    }
}
