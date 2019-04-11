using System;
using System.Collections.Generic;
using System.Text;

namespace PrivateBytesPerfCounterAnomalies.Domain.AppInsightsParsing
{
    public class Result
    {
        public string Name { get; set; }
        public List<string[]> Rows { get; set; }
    }
}
