using System;
using System.Reflection;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The patch "Skip winter": when Seasonality is about to start winter, it starts spring instead,
    /// so the seasons go spring, summer, fall, spring; every other change stays as it was.
    /// </summary>
    public sealed class SkipWinterTests
    {
        [Fact]
        public void Winter_coming_next_becomes_spring_and_the_others_stay()
        {
            Assert.Equal("Spring", SeasonSkip.Next("Winter"));
            Assert.Equal("Summer", SeasonSkip.Next("Summer"));
            Assert.Equal("Fall", SeasonSkip.Next("Fall"));
            Assert.Equal("Spring", SeasonSkip.Next("Spring"));
        }

        [Fact]
        public void The_state_says_off_working_stepped_aside_or_not_needed()
        {
            Assert.Equal("off: the server's switch", SeasonSkip.StateLine(false, true, "3.8.4", null));
            Assert.Equal("working · Seasonality 3.8.4 hooked, checked: fall now goes to spring", SeasonSkip.StateLine(true, true, "3.8.4", null));
            Assert.Equal("not needed: Seasonality is not installed", SeasonSkip.StateLine(true, false, null, null));
            Assert.Equal("stepped aside: the hook did not take: fall still goes to Winter", SeasonSkip.StateLine(true, true, "3.9.0", "the hook did not take: fall still goes to Winter"));
        }

        [Fact]
        public void Seasonality_385s_rule_is_found_and_asked_for_the_season_after_fall()
        {
            Season385.SeasonTime.m_currentSeason = Season385.Season.Summer;
            var rule = SeasonRule.Find(name => name == "Seasonality.SeasonTime" ? typeof(Season385.SeasonTime) : null);

            Assert.NotNull(rule);
            Assert.Equal(typeof(Season385.SeasonTime).GetMethod("GetNextSeason"), rule.Next);
            Assert.Equal("Winter", rule.AfterFall().ToString());
            Assert.Equal(Season385.Season.Summer, Season385.SeasonTime.m_currentSeason);
        }

        [Fact]
        public void Seasonality_384s_rule_is_still_found_and_asked_for_the_season_after_fall()
        {
            var rule = SeasonRule.Find(name => name == "Seasonality.SeasonalTimer" ? typeof(Season384.SeasonalTimer) : null);

            Assert.NotNull(rule);
            Assert.Equal(typeof(Season384.SeasonalTimer).GetMethod("GetNextSeason"), rule.Next);
            Assert.Equal("Winter", rule.AfterFall().ToString());
        }

        [Fact]
        public void A_rule_of_another_shape_or_no_Seasonality_types_is_not_taken()
        {
            Assert.Null(SeasonRule.Find(name => null));
            Assert.Null(SeasonRule.Find(name => name == "Seasonality.SeasonTime" ? typeof(OtherShape.SeasonTime) : null));
            Assert.Null(SeasonRule.Find(name => name == "Seasonality.SeasonalTimer" ? typeof(OtherShape.SeasonalTimer) : null));
        }

        [Fact]
        public void The_season_under_way_is_put_back_when_the_rule_throws()
        {
            Throwing.SeasonTime.m_currentSeason = Season385.Season.Spring;
            var rule = SeasonRule.Find(name => name == "Seasonality.SeasonTime" ? typeof(Throwing.SeasonTime) : null);

            Assert.NotNull(rule);
            Assert.Throws<TargetInvocationException>(() => rule.AfterFall());
            Assert.Equal(Season385.Season.Spring, Throwing.SeasonTime.m_currentSeason);
        }

        // Stand-ins with the shapes of Seasonality's next-season rule, by version.
        private static class Season385
        {
            public enum Season { Summer, Fall, Winter, Spring }

            public static class SeasonTime
            {
                public static Season m_currentSeason = Season.Summer;

                public static Season GetNextSeason() => m_currentSeason switch
                {
                    Season.Spring => Season.Summer,
                    Season.Summer => Season.Fall,
                    Season.Fall => Season.Winter,
                    _ => Season.Spring,
                };
            }
        }

        private static class Season384
        {
            public static class SeasonalTimer
            {
                public static Season385.Season GetNextSeason(Season385.Season season) => season == Season385.Season.Fall ? Season385.Season.Winter : Season385.Season.Summer;
            }
        }

        private static class OtherShape
        {
            public static class SeasonTime
            {
                public static int m_currentSeason;

                public static int GetNextSeason() => m_currentSeason + 1;
            }

            public static class SeasonalTimer
            {
                public static int GetNextSeason(int season) => season + 1;
            }
        }

        private static class Throwing
        {
            public static class SeasonTime
            {
                public static Season385.Season m_currentSeason;

                public static Season385.Season GetNextSeason() => throw new InvalidOperationException("no world");
            }
        }
    }
}
