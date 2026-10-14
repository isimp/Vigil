using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// Vigil replaces NetProbe, SpawnProof and Tidy. At its first start, what was set in their
    /// config files holds in Vigil's, under the section each setting has now; settings Vigil does
    /// not have stay behind, and the old files are only read.
    /// </summary>
    public sealed class ConfigCarryOverTests
    {
        private const string NetProbeCfg = @"## Settings file was created by plugin NetProbe v0.5.8
## Plugin GUID: isimp.NetProbe

[Deep capture]

## How long a deep capture runs.
# Setting type: Single
# Default value: 20
Seconds = 45

[General]

## Off: NetProbe puts in no hooks at all and records nothing, from the next start on.
# Setting type: Boolean
# Default value: true
Enabled = false

## On: records all the time.
# Setting type: RecordingMode
# Default value: Auto
# Acceptable values: On, Off, Auto
Recording = Off

[Incidents]

## A frame at least this long = a report.
# Setting type: Int32
# Default value: 250
LongFrameMs = 300

[Lag report]

# Setting type: KeyboardShortcut
# Default value: F9
Key = F8 + LeftControl

[Output]

Folder = D:\recordings=old
";

        private const string SpawnProofCfg = @"## Settings file was created by plugin SpawnProof v0.1.0
## Plugin GUID: isimp.SpawnProof

[General]

## Turns the view of spawn gaps on and off.
# Setting type: KeyCode
# Default value: F6
Key = F7

## How far around you spots are checked, in metres.
# Setting type: Single
# Default value: 40
# Acceptable value range: From 8 to 128
Radius = 64

# Setting type: Color
# Default value: FF331FBF
MarkColor = 22CC44BF
";

        private static Dictionary<(string Section, string Key), string> CarriedFrom(string netProbe, string spawnProof)
        {
            var old = new Dictionary<string, string>();
            if (netProbe != null) old[ConfigCarryOver.NetProbeFile] = netProbe;
            if (spawnProof != null) old[ConfigCarryOver.SpawnProofFile] = spawnProof;
            return ConfigCarryOver.Values(old);
        }

        [Fact]
        public void WhatWasSetInNetProbeHoldsInVigilUnderTheSameSection()
        {
            var v = CarriedFrom(NetProbeCfg, null);
            Assert.Equal("45", v[("Deep capture", "Seconds")]);
            Assert.Equal("300", v[("Incidents", "LongFrameMs")]);
            Assert.Equal("F8 + LeftControl", v[("Lag report", "Key")]);
        }

        [Fact]
        public void NetProbeSwitchedOffMeansVigilsRecorderStaysOff()
        {
            Assert.Equal("false", CarriedFrom(NetProbeCfg, null)[("Modules", "Recorder")]);
        }

        [Fact]
        public void TheRecordingSwitchKeepsItsSettingInItsOwnSection()
        {
            Assert.Equal("Off", CarriedFrom(NetProbeCfg, null)[("Recording", "Mode")]);
        }

        [Fact]
        public void SpawnMarkSettingsMoveToTheirOwnSection()
        {
            var v = CarriedFrom(null, SpawnProofCfg);
            Assert.Equal("F7", v[("Spawn marks", "Key")]);
            Assert.Equal("64", v[("Spawn marks", "Radius")]);
            Assert.Equal("22CC44BF", v[("Spawn marks", "MarkColor")]);
        }

        [Fact]
        public void TheTwoOldGeneralSectionsDoNotMix()
        {
            var v = CarriedFrom(NetProbeCfg, SpawnProofCfg);
            Assert.False(v.ContainsKey(("Spawn marks", "Enabled")));
            Assert.False(v.ContainsKey(("Spawn marks", "Recording")));
            Assert.False(v.ContainsKey(("Recording", "Radius")));
            Assert.False(v.ContainsKey(("General", "Key")));
            Assert.False(v.ContainsKey(("General", "Radius")));
        }

        [Fact]
        public void DescriptionsAndDefaultsAreNotTakenForSettings()
        {
            var read = ConfigCarryOver.Read(NetProbeCfg);
            Assert.DoesNotContain(read.Keys, k => k.Key.StartsWith("#", System.StringComparison.Ordinal));
            Assert.Equal("300", read[("Incidents", "LongFrameMs")]);
            Assert.Equal("45", read[("Deep capture", "Seconds")]);
        }

        [Fact]
        public void AValueWithAnEqualsSignInItSurvives()
        {
            Assert.Equal(@"D:\recordings=old", CarriedFrom(NetProbeCfg, null)[("Output", "Folder")]);
        }

        [Fact]
        public void ASettingVigilDoesNotHaveStaysBehind()
        {
            var v = CarriedFrom("[General]\nRetired = 3\n[Somewhere else]\nThing = 1\n", null);
            Assert.Empty(v);
        }

        [Fact]
        public void WithoutOldFilesNothingIsCarriedOver()
        {
            Assert.Empty(CarriedFrom(null, null));
        }

        [Fact]
        public void ADamagedOldFileGivesWhatCanStillBeRead()
        {
            var v = CarriedFrom("garbage line\n= no key\n[Incidents\nLongFrameMs = 1\n[Incidents]\nLongFrameMs = 400\nPingMs\n", null);
            Assert.Equal("400", v[("Incidents", "LongFrameMs")]);
            Assert.Single(v);
        }

        [Fact]
        public void TheNewFileCarriesTheValuesWhereVigilReadsThem()
        {
            string text = ConfigCarryOver.NewFileText(CarriedFrom(NetProbeCfg, SpawnProofCfg));
            Assert.Contains("[Spawn marks]", text);
            Assert.Contains("Radius = 64", text);
            Assert.Contains("[Modules]", text);
            Assert.Contains("Recorder = false", text);
            Assert.True(text.IndexOf("[Spawn marks]") < text.IndexOf("Radius = 64"));
            // read back the way the reader reads any config file: nothing lost, nothing added
            var again = ConfigCarryOver.Read(text);
            Assert.Equal(CarriedFrom(NetProbeCfg, SpawnProofCfg), again);
        }
    }
}
