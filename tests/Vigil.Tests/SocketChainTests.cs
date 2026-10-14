using System;
using System.Collections.Generic;
using Vigil.Core;
using Xunit;

namespace Vigil.Tests
{
    /// <summary>
    /// The server's per-player Steam numbers (ping, quality, rates, data waiting or unconfirmed)
    /// must fill even when other mods have wrapped the player's connection. ServerSync, bundled in
    /// many mods, wraps a joining player's socket in its own BufferingSocket; every bundled copy
    /// wraps the one before it and takes off only a wrapper of its own type, so wrappers can stay
    /// on for the whole session. Vigil looks through them to the Steam connection underneath,
    /// and says plainly when there is none.
    /// </summary>
    public sealed class SocketChainTests
    {
        public interface IFakeSocket { }

        private sealed class SteamSocket : IFakeSocket { }

        /// <summary>A connection that is not Steam's, e.g. a simulated player's.</summary>
        private sealed class OtherSocket : IFakeSocket { }

        /// <summary>Shaped like the game's ZPlayFabSocket, which ServerSync's wrapper derives from:
        /// a static listen socket and no wrapped socket of its own.</summary>
        private class PlayFabLikeSocket : IFakeSocket
        {
            public static IFakeSocket s_listenSocket = new SteamSocket();
            protected IFakeSocket m_unused;
        }

        /// <summary>Two bundled ServerSync copies: same shape, different types.</summary>
        private sealed class BufferingSocketA : PlayFabLikeSocket
        {
            public readonly IFakeSocket Original;
            public BufferingSocketA(IFakeSocket original) { Original = original; }
        }

        private sealed class BufferingSocketB : PlayFabLikeSocket
        {
            public readonly IFakeSocket Original;
            public BufferingSocketB(IFakeSocket original) { Original = original; }
        }

        private abstract class WrapperWithPrivateBase : IFakeSocket
        {
            private readonly IFakeSocket _inner;
            protected WrapperWithPrivateBase(IFakeSocket inner) { _inner = inner; }
        }

        private sealed class DerivedWrapper : WrapperWithPrivateBase
        {
            // an empty socket field of its own must not end the search before the base's
            public IFakeSocket m_spare;
            public DerivedWrapper(IFakeSocket inner) : base(inner) { }
        }

        private sealed class LoopingWrapper : IFakeSocket
        {
            public IFakeSocket Inner;
        }

        private static SocketChain.Result Find(object socket) =>
            SocketChain.Find(socket, typeof(IFakeSocket), s => s is SteamSocket);

        [Fact]
        public void APlainSteamConnectionIsReadAsItIs()
        {
            var steam = new SteamSocket();
            var r = Find(steam);
            Assert.Same(steam, r.Found);
            Assert.Empty(r.Wrappers);
        }

        [Fact]
        public void ASteamConnectionUnderAServerSyncWrapperIsFound()
        {
            var steam = new SteamSocket();
            var r = Find(new BufferingSocketA(steam));
            Assert.Same(steam, r.Found);
            Assert.Equal(new[] { typeof(BufferingSocketA) }, r.Wrappers);
        }

        [Fact]
        public void ASteamConnectionUnderEveryBundledServerSyncCopyIsFound()
        {
            var steam = new SteamSocket();
            IFakeSocket s = steam;
            for (int i = 0; i < 41; i++) s = i % 2 == 0 ? new BufferingSocketA(s) : (IFakeSocket)new BufferingSocketB(s);
            var r = Find(s);
            Assert.Same(steam, r.Found);
            Assert.Equal(41, r.Wrappers.Count);
        }

        [Fact]
        public void ASocketKeptInAPrivateFieldOfTheWrappersBaseClassIsFound()
        {
            var steam = new SteamSocket();
            Assert.Same(steam, Find(new DerivedWrapper(steam)).Found);
        }

        [Fact]
        public void WrappersAroundAnotherKindOfConnectionAreNotTakenForSteam()
        {
            var r = Find(new BufferingSocketA(new BufferingSocketB(new BufferingSocketA(new OtherSocket()))));
            Assert.Null(r.Found);
            Assert.Equal(SocketChain.Stop.NotTarget, r.Stopped);
            Assert.Equal("the connection is a OtherSocket under 3 wrappers (BufferingSocketA, BufferingSocketB), not a Steam socket", r.WhyNot("a Steam socket"));
        }

        [Fact]
        public void AnotherKindOfConnectionWithoutWrappersIsNamed()
        {
            Assert.Equal("the connection is a OtherSocket, not a Steam socket", Find(new OtherSocket()).WhyNot("a Steam socket"));
        }

        [Fact]
        public void AWrapperWhoseInnerSocketIsGoneFindsNothing()
        {
            // its base keeps a Steam socket in a static field, like the game's PlayFab socket; that
            // one belongs to no player and must not be taken for the wrapped connection
            var r = Find(new BufferingSocketA(null));
            Assert.Null(r.Found);
            Assert.Equal(SocketChain.Stop.NotTarget, r.Stopped);
        }

        [Fact]
        public void WrappersThatLeadBackToThemselvesDoNotHangTheRecorder()
        {
            var a = new LoopingWrapper();
            var b = new LoopingWrapper { Inner = a };
            a.Inner = b;
            var r = Find(a);
            Assert.Null(r.Found);
            Assert.Equal(SocketChain.Stop.Loop, r.Stopped);
            Assert.Equal("the wrappers around the connection lead back to themselves", r.WhyNot("a Steam socket"));
        }

        [Fact]
        public void AChainLongerThanTheLimitIsGivenUp()
        {
            IFakeSocket s = new SteamSocket();
            for (int i = 0; i <= SocketChain.MaxWrappers; i++) s = new BufferingSocketA(s);
            var r = Find(s);
            Assert.Null(r.Found);
            Assert.Equal(SocketChain.Stop.TooDeep, r.Stopped);
        }

        [Fact]
        public void AChainExactlyAtTheLimitIsStillFound()
        {
            var steam = new SteamSocket();
            IFakeSocket s = steam;
            for (int i = 0; i < SocketChain.MaxWrappers; i++) s = new BufferingSocketA(s);
            Assert.Same(steam, Find(s).Found);
        }

        [Fact]
        public void AMissingSocketIsNamed()
        {
            var r = Find(null);
            Assert.Null(r.Found);
            Assert.Equal(SocketChain.Stop.Missing, r.Stopped);
            Assert.Equal("the connection has no socket", r.WhyNot("a Steam socket"));
        }

        [Fact]
        public void TheEventLogNamesWhichModsLeftTheirWrappersOn()
        {
            var wrappers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("BufferingSocket", "Seasonality"),
                new KeyValuePair<string, string>("BufferingSocket", "TargetPortal"),
                new KeyValuePair<string, string>("OtherWrapper", "SomeMod"),
            };
            Assert.Equal("read through 3 wrappers: BufferingSocket from Seasonality, TargetPortal; OtherWrapper from SomeMod",
                SocketChain.Describe(wrappers));
        }

        [Fact]
        public void OneWrapperIsCountedInTheSingular()
        {
            var wrappers = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("BufferingSocket", "Hoard") };
            Assert.Equal("read through 1 wrapper: BufferingSocket from Hoard", SocketChain.Describe(wrappers));
        }

        [Fact]
        public void AConnectionWhoseWrappersAreAllOffSaysSo()
        {
            Assert.Equal("read directly, no wrappers", SocketChain.Describe(new List<KeyValuePair<string, string>>()));
        }
    }
}
