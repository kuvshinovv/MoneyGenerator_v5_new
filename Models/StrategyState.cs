using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public enum StrategyState
    {
        Stopped,
        Initializing,
        Running,
        Paused,
        Error
    }
}
