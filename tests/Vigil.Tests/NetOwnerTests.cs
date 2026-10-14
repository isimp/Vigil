using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// When the network is busy, the report must say which mod the traffic belongs to, not only
    /// what the messages are called: a message counts for the mod named in it, otherwise for the
    /// mod whose code handles it, otherwise for the game.
    /// </summary>
    public sealed class NetOwnerTests
    {
        private static readonly Dictionary<string, string> Guids = new Dictionary<string, string>
        {
            { "MidnightsFX.Deathlink", "Deathlink" },
            { "Azumatt.AzuCraftyBoxes", "AzuCraftyBoxes" },
            { "Azumatt.AzuCraftyBoxesExtra", "AzuCraftyBoxesExtra" },
        };

        private static readonly Dictionary<string, string> Assemblies = new Dictionary<string, string>
        {
            { "ShieldSledding", "Shield Sledding" },
            { "LongshipUpgrades", "Longship Upgrades" },
        };

        private static string Owner(string name, string handlerAssembly) => NetOwner.Resolve(name, handlerAssembly, Guids, Assemblies);

        [Fact]
        public void AJotunnMessageCountsForTheModNamedInIt()
        {
            Assert.Equal("Deathlink", Owner("MidnightsFX.Deathlink!DEATHLK_LB", "Jotunn"));
        }

        [Fact]
        public void AConfigSyncChannelCountsForItsMod()
        {
            Assert.Equal("AzuCraftyBoxes", Owner("Azumatt.AzuCraftyBoxes ConfigSync", "AzuCraftyBoxes"));
        }

        [Fact]
        public void AModWhoseIdStartsLikeAnotherIsNotConfusedWithIt()
        {
            Assert.Equal("AzuCraftyBoxesExtra", Owner("Azumatt.AzuCraftyBoxesExtra ConfigSync", null));
        }

        [Fact]
        public void OtherwiseTheModWhoseCodeHandlesTheMessageOwnsIt()
        {
            Assert.Equal("Shield Sledding", Owner("ShieldSledding_State", "ShieldSledding"));
        }

        [Fact]
        public void TheGamesOwnMessagesCountForTheGame()
        {
            Assert.Equal("Valheim", Owner("RPC_Damage", "assembly_valheim"));
            Assert.Equal("Valheim", Owner("ZDOData", "assembly_valheim"));
        }

        [Fact]
        public void AnUnknownHandlerIsNamedRatherThanGuessed()
        {
            Assert.Equal("unknown (SomeLibrary)", Owner("Thing", "SomeLibrary"));
            Assert.Equal("unknown", Owner("Thing", null));
        }

        [Fact]
        public void AModIdFollowedByMoreLettersIsADifferentName()
        {
            Assert.Equal("Shield Sledding", Owner("Azumatt.AzuCraftyBoxesOld ConfigSync", "ShieldSledding"));
        }

        [Fact]
        public void TheLongestMatchingModIdWinsWhateverOrderTheModsLoadedIn()
        {
            var guids = new Dictionary<string, string> { { "A.Mod2", "Mod Two" }, { "A.Mod", "Mod" } };
            var guids2 = new Dictionary<string, string> { { "A.Mod", "Mod" }, { "A.Mod2", "Mod Two" } };

            Assert.Equal("Mod Two", NetOwner.Resolve("A.Mod2 ConfigSync", null, guids, Assemblies));
            Assert.Equal("Mod Two", NetOwner.Resolve("A.Mod2 ConfigSync", null, guids2, Assemblies));
        }

        [Fact]
        public void AModIdInTheMiddleOfANameDoesNotCount()
        {
            var guids = new Dictionary<string, string> { { "Mod.A", "Mod A" } };

            Assert.Equal("Shield Sledding", NetOwner.Resolve("Mod.B Mod.A", "ShieldSledding", guids, Assemblies));
        }

        [Fact]
        public void ANameThatOnlyContainsAModIdLaterOnIsNotTakenForIt()
        {
            Assert.Equal("Shield Sledding", Owner("Sync MidnightsFX.Deathlink!x", "ShieldSledding"));
        }
    }
}
