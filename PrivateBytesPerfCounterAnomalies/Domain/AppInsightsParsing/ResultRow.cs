using System;
using System.Collections.Generic;
using System.Text;

namespace PrivateBytesPerfCounterAnomalies.Domain.AppInsightsParsing
{
    public class ResultRow
    {
        public DateTime Timestamp { get; set; }

        public long Value { get; set; }

    }
}
