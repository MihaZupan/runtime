// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace System.Diagnostics.Tracing
{
    /// <summary>Simple event listener than invokes a callback for each event received.</summary>
    internal sealed class TestEventListener : EventListener
    {
        private readonly Dictionary<string, (EventLevel Level, EventKeywords Keywords)> _names = new Dictionary<string, (EventLevel, EventKeywords)>();
        private readonly Dictionary<Guid, (EventLevel Level, EventKeywords Keywords)> _guids = new Dictionary<Guid, (EventLevel, EventKeywords)>();

        private readonly double? _eventCounterInterval;

        private Action<EventWrittenEventArgs> _eventWritten;
        private readonly List<EventSource> _eventSourceList = new List<EventSource>();

        public TestEventListener(string targetSourceName, EventLevel level, double? eventCounterInterval = null)
        {
            _eventCounterInterval = eventCounterInterval;
            AddSource(targetSourceName, level);
        }

        public TestEventListener(Guid targetSourceGuid, EventLevel level, double? eventCounterInterval = null)
        {
            _eventCounterInterval = eventCounterInterval;
            AddSource(targetSourceGuid, level);
        }

        public void AddSource(string name, EventLevel level, EventKeywords keywords = EventKeywords.All) =>
            AddSource(name, null, level, keywords);

        public void AddSource(Guid guid, EventLevel level, EventKeywords keywords = EventKeywords.All) =>
            AddSource(null, guid, level, keywords);

        private void AddSource(string name, Guid? guid, EventLevel level, EventKeywords keywords)
        {
            lock (_eventSourceList)
            {
                if (name is not null)
                    _names.Add(name, (level, keywords));

                if (guid.HasValue)
                    _guids.Add(guid.Value, (level, keywords));

                foreach (EventSource source in _eventSourceList)
                {
                    if (name == source.Name || guid == source.Guid)
                    {
                        EnableEventSource(source, level, keywords);
                    }
                }
            }
        }

        public void AddActivityTracking() =>
            AddSource("System.Threading.Tasks.TplEventSource", EventLevel.Informational, (EventKeywords)0x80 /* TasksFlowActivityIds */);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            lock (_eventSourceList)
            {
                _eventSourceList.Add(eventSource);

                if (_names.TryGetValue(eventSource.Name, out (EventLevel Level, EventKeywords Keywords) settings) ||
                    _guids.TryGetValue(eventSource.Guid, out settings))
                {
                    EnableEventSource(eventSource, settings.Level, settings.Keywords);
                }
            }
        }

        private void EnableEventSource(EventSource source, EventLevel level, EventKeywords keywords)
        {
            var args = new Dictionary<string, string>();

            if (_eventCounterInterval != null)
            {
                args.Add("EventCounterIntervalSec", _eventCounterInterval.ToString());
            }

            EnableEvents(source, level, keywords, args);
        }

        public void RunWithCallback(Action<EventWrittenEventArgs> handler, Action body)
        {
            _eventWritten = handler;
            try { body(); }
            finally { _eventWritten = null; }
        }

        public async Task RunWithCallbackAsync(Action<EventWrittenEventArgs> handler, Func<Task> body)
        {
            _eventWritten = handler;
            try { await body().ConfigureAwait(false); }
            finally { _eventWritten = null; }
        }

        // Workaround for being able to inspect the ActivityId property after storing EventWrittenEventArgs
        // [ActiveIssue("https://github.com/dotnet/runtime/issues/42128")]
        private static readonly FieldInfo s_activityIdFieldInfo =
            typeof(EventWrittenEventArgs).GetField("m_activityId", BindingFlags.NonPublic | BindingFlags.Instance);

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            s_activityIdFieldInfo.SetValue(eventData, eventData.ActivityId);
            _eventWritten?.Invoke(eventData);
        }
    }

}
