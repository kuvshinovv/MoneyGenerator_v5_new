using System;
using System.Collections.Generic;
using System.Text;

namespace MoneyGenerator_v5.Models
{
    public enum OrderStatus
    {
        Unknown,
        New,
        PartiallyFilled,
        Filled,
        Cancelled,
        Rejected,
        Pending,
        Expired,
        NotFound,
        Timeout
    }
}
