using System;
using System.Collections.Generic;

namespace EmbyProxyRouter.Proxy
{
    /// <summary>
    /// Collapses a repeated log event into one line per key per time window.
    /// </summary>
    /// <remarks>
    /// The gate writes a line for every request it blocks. That is the right behaviour for one
    /// request and the wrong behaviour for a library scan: with the proxy address misconfigured, a
    /// few thousand metadata lookups produce a few thousand identical lines, and the one line that
    /// mattered — the first — is buried in them.
    ///
    /// Throttling here is deliberately biased towards logging:
    ///
    ///   * The first sighting of a key is never suppressed. A new destination, or a known one for a
    ///     new reason, is a new event and reaches the log immediately.
    ///   * Suppressed occurrences are counted, and the count is reported on the next line the key
    ///     produces, so the log states how much it left out instead of quietly dropping it.
    ///   * When the key map is full, the event is logged rather than tracked. Running out of
    ///     bookkeeping space must not turn into silence.
    ///
    /// It is not a rate limiter for the sake of volume. "Never silently" is the property the plugin
    /// is built around, and none of the above weakens it: no event class ever goes unreported, it
    /// just stops being reported once per request.
    /// </remarks>
    public sealed class LogThrottle
    {
        /// <summary>
        /// Bounds the key map. Keys are per destination host and reason, so the live set is normally
        /// the handful of metadata providers Emby talks to; the cap exists for the pathological case
        /// (a scan touching thousands of distinct image hosts), not the expected one.
        /// </summary>
        private const int DefaultCapacity = 512;

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly TimeSpan _window;
        private readonly int _capacity;
        private readonly Func<DateTime> _clock;

        public LogThrottle(TimeSpan window)
            : this(window, DefaultCapacity, null)
        {
        }

        /// <summary>
        /// Full constructor. <paramref name="clock"/> is injectable so the window can be tested
        /// without waiting for it.
        /// </summary>
        public LogThrottle(TimeSpan window, int capacity, Func<DateTime> clock)
        {
            _window = window;
            _capacity = capacity < 1 ? 1 : capacity;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public TimeSpan Window
        {
            get { return _window; }
        }

        /// <summary>
        /// Returns true when the caller should write this event, and how many identical ones were
        /// suppressed since the last time it did.
        /// </summary>
        public bool ShouldLog(string key, out int suppressed)
        {
            suppressed = 0;

            if (key == null)
            {
                return true;
            }

            var now = _clock();

            lock (_sync)
            {
                Entry entry;
                if (_entries.TryGetValue(key, out entry))
                {
                    if (now - entry.WindowStart < _window)
                    {
                        entry.Suppressed++;
                        return false;
                    }

                    suppressed = entry.Suppressed;
                    entry.WindowStart = now;
                    entry.Suppressed = 0;
                    return true;
                }

                if (_entries.Count >= _capacity)
                {
                    Prune(now);

                    if (_entries.Count >= _capacity)
                    {
                        // Every tracked key is still inside its window, so there is nothing to evict
                        // that would not lose a live count. Log this one untracked: the throttle is
                        // here to reduce repetition, and failing it open costs duplicate lines,
                        // while failing it closed would hide an event outright.
                        return true;
                    }
                }

                _entries[key] = new Entry { WindowStart = now };
                return true;
            }
        }

        /// <summary>
        /// Drops keys whose window has elapsed.
        /// </summary>
        /// <remarks>
        /// An expired entry would log on its next occurrence anyway, so forgetting it changes only
        /// the "+N suppressed" note it was still carrying. That understates repetition in an already
        /// pathological case; it never withholds an event.
        ///
        /// Called under <c>_sync</c>, and only when the map is full — a scan of a few hundred entries
        /// on the rare occasion the cap is hit, not per request.
        /// </remarks>
        private void Prune(DateTime now)
        {
            List<string> expired = null;

            foreach (var pair in _entries)
            {
                if (now - pair.Value.WindowStart >= _window)
                {
                    if (expired == null)
                    {
                        expired = new List<string>();
                    }

                    expired.Add(pair.Key);
                }
            }

            if (expired == null)
            {
                return;
            }

            for (var i = 0; i < expired.Count; i++)
            {
                _entries.Remove(expired[i]);
            }
        }

        private sealed class Entry
        {
            public DateTime WindowStart;
            public int Suppressed;
        }
    }
}
