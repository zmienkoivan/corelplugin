using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class WorkTimeSettings
    {
        public string Employee { get; set; } = "";
        public string ComputerId { get; set; } = Guid.NewGuid().ToString("N");
        public bool Enabled { get; set; }
        public int IdleMinutes { get; set; } = 5;
    }
    internal sealed class WorkTimeEvent
    {
        public string AtUtc { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public string Detail { get; set; }
        public double DurationSeconds { get; set; }
        internal WorkTimeEvent Copy() => (WorkTimeEvent)MemberwiseClone();
    }
    internal sealed class WorkTimeSession
    {
        public int Schema { get; set; } = 1;
        public string SessionId { get; set; }
        public string ProjectId { get; set; }
        public string Employee { get; set; }
        public string ComputerId { get; set; }
        public string Computer { get; set; }
        public int ProcessId { get; set; }
        public string ProcessStartedUtc { get; set; }
        public string FilePath { get; set; }
        public string Title { get; set; }
        public string StartedUtc { get; set; }
        public string UpdatedUtc { get; set; }
        public string ClosedUtc { get; set; }
        public int Revision { get; set; }
        public double ActiveSeconds { get; set; }
        public double BackgroundSeconds { get; set; }
        public double IdleSeconds { get; set; }
        public double UnobservedSeconds { get; set; }
        public int OmittedEvents { get; set; }
        public List<WorkTimeEvent> Events { get; set; } = new List<WorkTimeEvent>();
        public string[] JournalFolders { get; set; } = new string[0];
        internal WorkTimeSession Copy()
        {
            var value = (WorkTimeSession)MemberwiseClone();
            value.Events = Events.Select(item => item.Copy()).ToList();
            value.JournalFolders = (JournalFolders ?? new string[0]).ToArray(); return value;
        }
        internal double OpenSeconds => ActiveSeconds + BackgroundSeconds + IdleSeconds + UnobservedSeconds;
    }
    internal sealed class WorkDocumentSnapshot
    {
        internal string Key, ProjectId, FilePath, Title;
        internal string[] JournalFolders = new string[0];
    }
    internal sealed class WorkTimePoint
    {
        internal double MonotonicSeconds;
        internal DateTime Utc;
        internal string ActiveKey;
        internal bool Foreground, Idle, Paused;
    }
}
