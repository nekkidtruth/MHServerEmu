﻿namespace MHServerEmu.Core.Metrics.Entries
{
    public readonly struct ReportFloatEntry
    {
        public float Min { get; }
        public float Max { get; }
        public float Average { get; }
        public float Median { get; }

        public ReportFloatEntry(float min, float max, float average, float median)
        {
            Min = min;
            Max = max;
            Average = average;
            Median = median;
        }

        public override string ToString()
        {
            return $"min={Min}, max={Max}, avg={Average}, mdn={Median}";
        }
    }
}