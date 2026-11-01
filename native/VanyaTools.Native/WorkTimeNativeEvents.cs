using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    internal sealed class WorkTimeNativeEvents : IDisposable
    {
        internal static readonly Guid ApplicationEvents = new Guid("B05800C5-9AA4-44FD-9547-4F91EB757AC4");
        internal static readonly Guid DocumentEvents = new Guid("B05800C6-9AA4-44FD-9547-4F91EB757AC4");
        private readonly object _source;
        private readonly Guid _iid;
        private readonly List<Tuple<int, Delegate>> _handlers = new List<Tuple<int, Delegate>>();
        internal bool Available => _handlers.Count > 0;
        internal WorkTimeNativeEvents(object source, Guid iid) { _source = source; _iid = iid; }
        internal void Add(int id, Delegate handler)
        {
            if (!Marshal.IsComObject(_source)) return;
            ComEventsHelper.Combine(_source, _iid, id, handler); _handlers.Add(Tuple.Create(id, handler));
        }
        public void Dispose()
        {
            foreach (var value in _handlers)
                try { ComEventsHelper.Remove(_source, _iid, value.Item1, value.Item2); }
                catch (Exception error) { Log.Error("Could not detach Corel work-time event.", error); }
            _handlers.Clear();
        }
    }
}
