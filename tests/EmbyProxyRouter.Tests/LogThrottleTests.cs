using System;
using EmbyProxyRouter.Proxy;
using Xunit;

namespace EmbyProxyRouter.Tests
{
    /// <summary>
    /// The throttle is allowed to make the log shorter. It is not allowed to make it incomplete —
    /// these tests are mostly about the second half of that.
    /// </summary>
    public class LogThrottleTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        /// <summary>A hand-cranked clock, so the window can be tested without waiting for it.</summary>
        private sealed class TestClock
        {
            private DateTime _now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            public DateTime Now()
            {
                return _now;
            }

            public void Advance(TimeSpan by)
            {
                _now += by;
            }
        }

        [Fact]
        public void TheFirstOccurrenceOfAKeyIsNeverSuppressed()
        {
            var throttle = new LogThrottle(Window);

            int suppressed;
            Assert.True(throttle.ShouldLog("a", out suppressed));
            Assert.Equal(0, suppressed);
        }

        [Fact]
        public void RepeatsWithinTheWindowAreSuppressed()
        {
            var throttle = new LogThrottle(Window);

            int suppressed;
            Assert.True(throttle.ShouldLog("a", out suppressed));

            for (var i = 0; i < 500; i++)
            {
                Assert.False(throttle.ShouldLog("a", out suppressed));
            }
        }

        /// <summary>
        /// What was left out has to be stated. A silently shortened log is the failure mode this
        /// whole plugin is built to avoid, and it would be perverse to introduce one in the logging.
        /// </summary>
        [Fact]
        public void TheNextLineReportsHowManyWereSuppressed()
        {
            var clock = new TestClock();
            var throttle = new LogThrottle(Window, 128, clock.Now);

            int suppressed;
            Assert.True(throttle.ShouldLog("a", out suppressed));

            for (var i = 0; i < 42; i++)
            {
                Assert.False(throttle.ShouldLog("a", out suppressed));
            }

            clock.Advance(Window + TimeSpan.FromSeconds(1));

            Assert.True(throttle.ShouldLog("a", out suppressed));
            Assert.Equal(42, suppressed);
        }

        [Fact]
        public void TheCountResetsAfterItIsReported()
        {
            var clock = new TestClock();
            var throttle = new LogThrottle(Window, 128, clock.Now);

            int suppressed;
            throttle.ShouldLog("a", out suppressed);
            throttle.ShouldLog("a", out suppressed);

            clock.Advance(Window + TimeSpan.FromSeconds(1));
            Assert.True(throttle.ShouldLog("a", out suppressed));
            Assert.Equal(1, suppressed);

            clock.Advance(Window + TimeSpan.FromSeconds(1));
            Assert.True(throttle.ShouldLog("a", out suppressed));
            Assert.Equal(0, suppressed);
        }

        /// <summary>
        /// Keys are independent: a new destination, or a known one for a new reason, is a new event
        /// and must not be swallowed by a window another key opened.
        /// </summary>
        [Fact]
        public void KeysDoNotShareAWindow()
        {
            var throttle = new LogThrottle(Window);

            int suppressed;
            Assert.True(throttle.ShouldLog("a", out suppressed));
            Assert.True(throttle.ShouldLog("b", out suppressed));
            Assert.True(throttle.ShouldLog("c", out suppressed));

            Assert.False(throttle.ShouldLog("a", out suppressed));
            Assert.False(throttle.ShouldLog("b", out suppressed));
        }

        /// <summary>
        /// Running out of bookkeeping space must degrade to more logging, never to less.
        /// </summary>
        [Fact]
        public void AFullKeyMapLogsRatherThanDrops()
        {
            var clock = new TestClock();
            var throttle = new LogThrottle(Window, 4, clock.Now);

            int suppressed;
            for (var i = 0; i < 4; i++)
            {
                Assert.True(throttle.ShouldLog("key" + i, out suppressed));
            }

            // The map is full and every entry is still inside its window, so this key cannot be
            // tracked. It gets logged anyway - repeatedly, which is the acceptable failure.
            Assert.True(throttle.ShouldLog("overflow", out suppressed));
            Assert.True(throttle.ShouldLog("overflow", out suppressed));
        }

        /// <summary>
        /// Once the tracked keys go quiet their slots come back, so a burst of distinct destinations
        /// does not disable throttling permanently.
        /// </summary>
        [Fact]
        public void ExpiredKeysFreeTheirSlots()
        {
            var clock = new TestClock();
            var throttle = new LogThrottle(Window, 4, clock.Now);

            int suppressed;
            for (var i = 0; i < 4; i++)
            {
                throttle.ShouldLog("key" + i, out suppressed);
            }

            clock.Advance(Window + TimeSpan.FromSeconds(1));

            Assert.True(throttle.ShouldLog("late", out suppressed));
            Assert.False(throttle.ShouldLog("late", out suppressed));
        }

        [Fact]
        public void ANullKeyIsAlwaysLogged()
        {
            var throttle = new LogThrottle(Window);

            int suppressed;
            Assert.True(throttle.ShouldLog(null, out suppressed));
            Assert.True(throttle.ShouldLog(null, out suppressed));
        }

        [Fact]
        public void TheWindowIsReadableForTheMessageThatQuotesIt()
        {
            Assert.Equal(Window, new LogThrottle(Window).Window);
        }
    }
}
