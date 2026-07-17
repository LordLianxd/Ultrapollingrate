using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class ProfileStoreTests : IDisposable
    {
        private readonly string _dir;

        public ProfileStoreTests()
        {
            // A real temp directory, not a mock: this class exists to touch the disk, so
            // testing it against a fake would test nothing.
            _dir = Path.Combine(Path.GetTempPath(), "ultrapolling-tests-" + Guid.NewGuid().ToString("N"));
            ProfileStore.OverrideDirectoryForTests(_dir);
        }

        public void Dispose()
        {
            ProfileStore.OverrideDirectoryForTests(null);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static LightProfile Sample(string name = "CoD") => new LightProfile
        {
            Name = name,
            Rate = 1000,
            R = 255, G = 100, B = 0,
            Player = PlayerLeds.Player1,
            Brightness = LedBrightness.High,
            Rainbow = false
        };

        [Fact]
        public void Load_WithNoFile_IsEmpty_NotAnException()
        {
            Assert.Empty(ProfileStore.Load());
        }

        [Fact]
        public void SaveThenLoad_RoundTripsEveryField()
        {
            var saved = ProfileStore.Save(new[] { Sample() });
            Assert.True(saved.Success, saved.Error);

            var loaded = ProfileStore.Load().Single();
            Assert.Equal("CoD", loaded.Name);
            Assert.Equal(1000, loaded.Rate);
            Assert.Equal(255, loaded.R);
            Assert.Equal(100, loaded.G);
            Assert.Equal(0, loaded.B);
            Assert.Equal(PlayerLeds.Player1, loaded.Player);
            Assert.Equal(LedBrightness.High, loaded.Brightness);
            Assert.False(loaded.Rainbow);
        }

        // A profile that does not touch the rate is a real case - "just make it red".
        [Fact]
        public void NullRate_SurvivesTheRoundTrip()
        {
            var p = Sample();
            p.Rate = null;
            ProfileStore.Save(new[] { p });
            Assert.Null(ProfileStore.Load().Single().Rate);
        }

        [Fact]
        public void Save_OverwritesRatherThanAppending()
        {
            ProfileStore.Save(new[] { Sample("uno"), Sample("dos") });
            ProfileStore.Save(new[] { Sample("tres") });

            var loaded = ProfileStore.Load();
            Assert.Single(loaded);
            Assert.Equal("tres", loaded[0].Name);
        }

        // DSX backs up its save file before every write, and it is the cheapest possible
        // insurance: a crash mid-write would otherwise lose every profile the user has.
        [Fact]
        public void Save_BacksUpThePreviousFileFirst()
        {
            ProfileStore.Save(new[] { Sample("original") });
            ProfileStore.Save(new[] { Sample("reemplazo") });

            string backup = ProfileStore.Path + ".backup";
            Assert.True(File.Exists(backup), "no backup was written");
            Assert.Contains("original", File.ReadAllText(backup));
        }

        // The first write has nothing to back up. Copying a non-existent file would throw
        // and lose the very first profile the user ever saves.
        [Fact]
        public void Save_FirstEverWrite_NeedsNoBackup()
        {
            var result = ProfileStore.Save(new[] { Sample() });
            Assert.True(result.Success, result.Error);
            Assert.False(File.Exists(ProfileStore.Path + ".backup"), "backed up a file that did not exist yet");
        }

        // A corrupt file must not take the app down on startup. Losing the profiles is
        // bad; refusing to launch is worse.
        [Fact]
        public void Load_WithCorruptJson_IsEmpty_NotAnException()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ProfileStore.Path, "{ this is not json");
            Assert.Empty(ProfileStore.Load());
        }

        [Fact]
        public void Load_WithAnEmptyFile_IsEmpty()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ProfileStore.Path, "");
            Assert.Empty(ProfileStore.Load());
        }

        [Fact]
        public void Save_CreatesTheDirectoryIfItIsMissing()
        {
            Assert.False(Directory.Exists(_dir));
            Assert.True(ProfileStore.Save(new[] { Sample() }).Success);
            Assert.True(File.Exists(ProfileStore.Path));
        }
    }
}
