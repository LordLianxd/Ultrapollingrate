# A Rainbow That Walks, Not Jumps — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the rainbow advance one colour at a time — `+1, +1, +1` — never skipping a value, so the lightbar glides instead of stepping.

**Architecture:** Precompute each style's ordered ramp of every distinct colour it can produce, once. The tick then walks an index through that ramp instead of computing a colour from elapsed time. The user's speed control becomes colours-per-second, and the UI states the cycle time that speed implies rather than the other way round.

**Tech Stack:** .NET 9 (`net9.0-windows`), WPF.

## Why the current design is wrong, in one sentence

`ColourAt(seconds, cycle, style)` computes the colour **from elapsed time**, which guarantees the cycle lasts exactly `cycle` seconds *and skips colours to achieve it*.

That is the wrong invariant for a lightbar. Nobody can tell whether a cycle took 30.0 s or 31.4 s. Everybody can see a colour that got skipped.

Two consequences, both measured:

- **Arithmetic skipping.** A `Vivid` cycle contains **1530 distinct colours**. At 30 ticks/second a 30 s cycle has 900 frames. 900 < 1530, so it *cannot* show them all — it drops ~40% by construction, before any timer misbehaves.
- **Dropped ticks become jumps.** `new DispatcherTimer()` runs at `DispatcherPriority.Background`, below Render and Input. When the UI is busy the tick is late, and a time-driven formula answers a late question with a *further* colour. Measured: 1 dropped tick doubles the step, 3 dropped ticks make it 7.

Walking an index is immune to both. A late tick makes the walk momentarily slower; it can never make it jump. And the step between consecutive ramp entries is 1 by construction, so "no gaps" stops being a hope and becomes a property a test can assert.

## The measurements this plan is built on

Sampled at 100× any frame rate, against the shipping `RainbowEffect`:

```
             distinct   max step between      max step per frame
             colours    consecutive colours   at 30 Hz / 30 s cycle
Vivid          1530              1                     2
Smooth          681              1                     1
Balanced       1364              1                    17    <- a real defect
```

**`Balanced` jumps 17 in one frame** (`#13AD00 → #02AE00`). Its chroma comes from `MaxChroma`, which changes fast with hue in the greens, so equal hue steps are not equal colour steps. Walking the ramp fixes this for free — the ramp is built from distinct colours, not from equal hue increments.

The "max step between consecutive colours = 1" column is what makes this plan possible: every style's ramp *is* already a continuous walk when sampled finely enough. The bug is only that a time-driven tick samples it too coarsely.

## What the speed control means now

At one colour per tick, the cycle time falls out of the ramp length rather than being chosen:

```
             colours    30/s       60/s      120/s
Vivid         1530      51.0s      25.5s     12.8s
Smooth         681      22.7s      11.3s      5.7s
Balanced      1364      45.5s      22.7s     11.4s
```

So **speed sets how often the timer fires**, and every tick is `+1`. Nothing in the tick knows what time it is. This is what makes a late tick harmless: it still advances one colour, and the walk merely runs a fraction slow — where the old code answered a late tick by jumping further.

The UI must *state* the resulting cycle time, not let the user ask for one that is a lie.

## Global Constraints

- **`SystemManager.cs`, `DualSenseLight.cs`, `PollingCore.cs`, `LightProfile.cs` and `ColourPicker.xaml*` must not change.** Not one line.
- **`ColourMath`'s existing functions must not change** — `HsvToRgb`, `RgbToHsv`, `RgbToOklab`, `OklchToRgb`, `OklchInGamut`, `MaxChroma`. The picker and the ramp both depend on them.
- **`RainbowEffect.ColourAt` must not change and must keep its tests green.** The ramp is built *from* it. It stops being what the tick calls; it does not stop existing.
- The 209 existing tests must stay green **without being modified**.
- Palette — exactly these ten, no eleventh: `#000000`, `#0A0A0A`, `#111111`, `#1F1F1F`, `#FFFFFF`, `#8A8A8A`, `#4A4A4A`, `#00C853`, `#FFAB00`, `#FF3D00`.
- **Colour never decorates.** The picker, presets and swatch are the only exceptions.
- `ColourMath.cs` and any new pure file must stay free of WPF — the test project links them by path precisely so it never pulls WPF in.
- Target framework `net9.0-windows`. Branch `redesign/monochrome`. Build from `HidusbfModernGui/`.
- Git identity is not configured globally. Commit with:
  `git -c user.name="UltraPolling" -c user.email="calizayacristhian96@gmail.com" commit -m "..."`
  End every commit message with: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

### Task 1: `ColourRamp` — the ordered walk, with no gaps by construction

**Files:**
- Create: `HidusbfModernGui/ColourRamp.cs`
- Test: `HidusbfModernGui.Tests/ColourRampTests.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`

**Interfaces:**
- Consumes: `RainbowEffect.ColourAt(double seconds, double cycleSeconds, RainbowStyle style)`, `RainbowStyle` — both exist
- Produces (consumed by Task 2):
  - `sealed class ColourRamp` with `IReadOnlyList<(byte R, byte G, byte B)> Colours { get; }` and `int Count { get; }`
  - `static ColourRamp ColourRamp.For(RainbowStyle style)` — cached per style
  - `(byte R, byte G, byte B) this[int index]` — wraps, so any index is valid

- [ ] **Step 1: Write the failing tests**

Create `HidusbfModernGui.Tests/ColourRampTests.cs`:

```csharp
using System;
using System.Linq;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class ColourRampTests
    {
        // THE property this whole feature exists for. Every consecutive pair differs by at
        // most 1 in each channel - so walking the ramp cannot skip a colour, whatever the
        // timer does. If this fails, the rainbow steps.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void EveryStepIsAtMostOne(RainbowStyle style)
        {
            var ramp = ColourRamp.For(style);
            for (int i = 1; i < ramp.Count; i++)
            {
                var a = ramp[i - 1];
                var b = ramp[i];
                int step = Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
                Assert.True(step <= 1,
                    $"{style} jumps {step} at index {i}: #{a.R:X2}{a.G:X2}{a.B:X2} -> #{b.R:X2}{b.G:X2}{b.B:X2}");
            }
        }

        // It is a loop, so the seam matters as much as the middle. A ramp that walks
        // smoothly and then snaps on the wrap is still a rainbow that jumps - once a cycle,
        // at the same place, which is worse than random.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void TheLoopClosesWithoutASnap(RainbowStyle style)
        {
            var ramp = ColourRamp.For(style);
            var last = ramp[ramp.Count - 1];
            var first = ramp[0];
            int step = Math.Max(Math.Abs(last.R - first.R), Math.Max(Math.Abs(last.G - first.G), Math.Abs(last.B - first.B)));
            Assert.True(step <= 1,
                $"{style} snaps {step} on the wrap: #{last.R:X2}{last.G:X2}{last.B:X2} -> #{first.R:X2}{first.G:X2}{first.B:X2}");
        }

        // No colour twice: a repeat would mean the walk stalls, and the light would visibly
        // hesitate at that point every cycle.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void NoColourAppearsTwice(RainbowStyle style)
        {
            var ramp = ColourRamp.For(style);
            var keys = ramp.Colours.Select(c => (c.R << 16) | (c.G << 8) | c.B).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        // Measured against the shipping RainbowEffect. If a ramp collapses to a handful of
        // entries the walk is not covering the wheel, and every other test here would still
        // pass on a two-colour ramp.
        [Theory]
        [InlineData(RainbowStyle.Vivid, 1400)]
        [InlineData(RainbowStyle.Smooth, 600)]
        [InlineData(RainbowStyle.Balanced, 1200)]
        public void TheRampIsAsLongAsTheStyleHasColours(RainbowStyle style, int atLeast)
        {
            Assert.True(ColourRamp.For(style).Count >= atLeast,
                $"{style} only has {ColourRamp.For(style).Count} colours");
        }

        // The walk runs forever in both directions; the caller should never have to think
        // about the boundary.
        [Fact]
        public void TheIndexerWraps()
        {
            var ramp = ColourRamp.For(RainbowStyle.Smooth);
            Assert.Equal(ramp[0], ramp[ramp.Count]);
            Assert.Equal(ramp[1], ramp[ramp.Count + 1]);
            Assert.Equal(ramp[ramp.Count - 1], ramp[-1]);
        }

        // Building a ramp walks hundreds of thousands of samples. Doing that on every tick
        // would put it on the UI thread 30 times a second.
        [Fact]
        public void RampsAreCachedPerStyle()
        {
            Assert.Same(ColourRamp.For(RainbowStyle.Vivid), ColourRamp.For(RainbowStyle.Vivid));
            Assert.NotSame(ColourRamp.For(RainbowStyle.Vivid), ColourRamp.For(RainbowStyle.Smooth));
        }

        // Vivid's ramp must still be the vivid colours - the ramp is a resampling of
        // ColourAt, not a new palette.
        [Fact]
        public void VividStartsAtRed()
        {
            Assert.Equal(((byte)255, (byte)0, (byte)0), ColourRamp.For(RainbowStyle.Vivid)[0]);
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'ColourRamp' does not exist`.

- [ ] **Step 3: Implement**

Create `HidusbfModernGui/ColourRamp.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace HidusbfModernGui
{
    // Every distinct colour a style produces, in the order it produces them.
    //
    // The rainbow used to compute its colour from elapsed time, which holds the cycle
    // duration exact and skips colours to do it - a Vivid cycle has 1530 distinct colours
    // and a 30-second cycle at 30 ticks/second only has 900 frames to show them in. It also
    // meant a late tick produced a further colour, so DispatcherTimer's Background priority
    // turned UI load into visible jumps.
    //
    // Walking this list instead inverts the invariant: the colours are exact and the cycle
    // takes however long it takes. Nobody can see that a cycle ran 31.4s instead of 30.0s.
    // Everybody can see a colour that got skipped.
    public sealed class ColourRamp
    {
        private static readonly Dictionary<RainbowStyle, ColourRamp> Cache = new();
        private static readonly object CacheLock = new();

        // Fine enough that consecutive samples never differ by more than one 8-bit step,
        // for every style. Balanced is the demanding one: its chroma changes fast with hue
        // in the greens, so equal hue steps are not equal colour steps.
        private const int Samples = 360_000;

        public IReadOnlyList<(byte R, byte G, byte B)> Colours { get; }
        public int Count => Colours.Count;

        // Wraps in both directions, so a walker never has to think about the boundary.
        public (byte R, byte G, byte B) this[int index]
        {
            get
            {
                int i = index % Count;
                if (i < 0) i += Count;
                return Colours[i];
            }
        }

        private ColourRamp(IReadOnlyList<(byte, byte, byte)> colours) => Colours = colours;

        public static ColourRamp For(RainbowStyle style)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(style, out var cached)) return cached;

                var ramp = Build(style);
                Cache[style] = ramp;
                return ramp;
            }
        }

        // Resamples RainbowEffect.ColourAt densely and keeps each colour the first time it
        // appears. The result is the same palette in the same order, minus the duplicates a
        // dense sample produces.
        private static ColourRamp Build(RainbowStyle style)
        {
            var colours = new List<(byte, byte, byte)>();
            var seen = new HashSet<int>();

            // The period is arbitrary: sampling t across one period covers the hue circle
            // exactly once, whatever the number.
            const double period = 1.0;

            for (int i = 0; i < Samples; i++)
            {
                var c = RainbowEffect.ColourAt(i / (double)Samples * period, period, style);
                int key = (c.R << 16) | (c.G << 8) | c.B;
                if (seen.Add(key)) colours.Add((c.R, c.G, c.B));
            }

            return new ColourRamp(colours);
        }
    }
}
```

- [ ] **Step 4: Link it into the test project**

Add to the existing `<ItemGroup>` in `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj` that links `ColourMath.cs`:

```xml
    <Compile Include="..\HidusbfModernGui\ColourRamp.cs" Link="ColourRamp.cs" />
```

- [ ] **Step 5: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 224` (209 existing + 15 new). Any other number, stop and report.

**If `EveryStepIsAtMostOne` or `TheLoopClosesWithoutASnap` fails, do not raise `Samples` to make it pass without saying so.** A ramp that needs a finer sample than 360,000 to be continuous is telling you something about the style, and the failure message names the exact colours — report them.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/ColourRamp.cs HidusbfModernGui.Tests/ColourRampTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: a colour ramp with no gaps by construction

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: `RainbowWalker` — advance by one, forever

**Files:**
- Create: `HidusbfModernGui/RainbowWalker.cs`
- Test: `HidusbfModernGui.Tests/RainbowWalkerTests.cs`
- Modify: `HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj`

**Interfaces:**
- Consumes: `ColourRamp` (Task 1), `RainbowStyle`
- Produces (consumed by Task 3):
  - `sealed class RainbowWalker`
  - `RainbowWalker(RainbowStyle style)`
  - `(byte R, byte G, byte B) Step()` — returns the current colour and advances by exactly one
  - `void Reset()`
  - `static TimeSpan IntervalFor(double coloursPerSecond)` — how often to Step at that speed
  - `double CycleSeconds(double coloursPerSecond)` — the nominal loop time at that speed

**`Step()` takes no time argument, and that is the entire design.** Speed is expressed by
*how often* it is called, never by how far it moves. A tick that arrives late still advances
one colour — the walk slows for that instant and skips nothing. Any signature that accepts
elapsed seconds reintroduces the defect: it would answer a late tick by moving further.

- [ ] **Step 1: Write the failing tests**

Create `HidusbfModernGui.Tests/RainbowWalkerTests.cs`:

```csharp
using System;
using HidusbfModernGui;
using Xunit;

namespace HidusbfModernGui.Tests
{
    public class RainbowWalkerTests
    {
        // The ask, literally: +1, +1, +1. Every colour, in order, none unshown - the first
        // Step returns the first colour rather than jumping past it.
        [Fact]
        public void EachStepReturnsTheNextColour()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            var ramp = ColourRamp.For(RainbowStyle.Smooth);

            for (int i = 0; i < 20; i++)
                Assert.Equal(ramp[i], walker.Step());
        }

        // The whole feature, asserted end to end: walk three full loops and prove no two
        // consecutive colours differ by more than 1 - including across the seam. This is
        // what "que no haiga huecos" means when the walker and the ramp are combined.
        [Theory]
        [InlineData(RainbowStyle.Vivid)]
        [InlineData(RainbowStyle.Smooth)]
        [InlineData(RainbowStyle.Balanced)]
        public void WalkingNeverGaps(RainbowStyle style)
        {
            var walker = new RainbowWalker(style);
            var previous = walker.Step();

            for (int i = 0; i < ColourRamp.For(style).Count * 3; i++)
            {
                var next = walker.Step();
                int step = Math.Max(Math.Abs(previous.R - next.R),
                           Math.Max(Math.Abs(previous.G - next.G), Math.Abs(previous.B - next.B)));
                Assert.True(step <= 1,
                    $"{style} gapped {step} at step {i}: #{previous.R:X2}{previous.G:X2}{previous.B:X2} -> #{next.R:X2}{next.G:X2}{next.B:X2}");
                previous = next;
            }
        }

        [Fact]
        public void ItLoopsForever()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            int count = ColourRamp.For(RainbowStyle.Smooth).Count;

            var first = walker.Step();
            for (int i = 1; i < count; i++) walker.Step();
            Assert.Equal(first, walker.Step());
        }

        [Fact]
        public void EachStyleWalksItsOwnRamp()
        {
            Assert.Equal(ColourRamp.For(RainbowStyle.Vivid)[0], new RainbowWalker(RainbowStyle.Vivid).Step());
            Assert.Equal(ColourRamp.For(RainbowStyle.Balanced)[0], new RainbowWalker(RainbowStyle.Balanced).Step());
        }

        [Fact]
        public void Reset_ReturnsToTheStart()
        {
            var walker = new RainbowWalker(RainbowStyle.Vivid);
            walker.Step();
            walker.Step();
            walker.Reset();
            Assert.Equal(ColourRamp.For(RainbowStyle.Vivid)[0], walker.Step());
        }

        // Speed is expressed as how OFTEN to step, never how far. 30 colours/second is a
        // step every 33.3ms.
        [Theory]
        [InlineData(30, 33.333)]
        [InlineData(5, 200.0)]
        [InlineData(120, 8.333)]
        public void IntervalFor_TurnsSpeedIntoATickPeriod(double coloursPerSecond, double expectedMs)
        {
            Assert.Equal(expectedMs, RainbowWalker.IntervalFor(coloursPerSecond).TotalMilliseconds, 2);
        }

        // Feeds a DispatcherTimer.Interval, which throws on zero or negative. The slider
        // cannot produce those today, but a profile or a future edit could, and the throw
        // would land inside a timer tick and take the app down.
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void IntervalFor_NonsenseSpeed_IsClampedRatherThanThrowing(double speed)
        {
            Assert.True(RainbowWalker.IntervalFor(speed) > TimeSpan.Zero);
        }

        // The cycle time is an OUTPUT now, not an input: it falls out of the ramp length
        // and the speed. Measured: Smooth has 681 colours, so 30/s is ~22.7s.
        [Fact]
        public void CycleSeconds_FallsOutOfTheRampLength()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            int count = ColourRamp.For(RainbowStyle.Smooth).Count;

            Assert.Equal(count / 30.0, walker.CycleSeconds(30), 3);
            Assert.Equal(count / 10.0, walker.CycleSeconds(10), 3);
        }

        [Fact]
        public void CycleSeconds_NonsenseSpeed_IsInfinite_NotADivideByZero()
        {
            Assert.True(double.IsInfinity(new RainbowWalker(RainbowStyle.Vivid).CycleSeconds(0)));
        }
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL — `The name 'RainbowWalker' does not exist`.

- [ ] **Step 3: Implement**

Create `HidusbfModernGui/RainbowWalker.cs`:

```csharp
using System;

namespace HidusbfModernGui
{
    // Walks a ColourRamp one entry at a time.
    //
    // Step() takes no time argument, and that is the entire design. The old rainbow computed
    // its colour from elapsed time, so a late tick was answered with a FURTHER colour - and
    // ticks are late, because DispatcherTimer runs at Background priority, below Render and
    // Input. Measured: one dropped tick doubled the jump, three made it seven.
    //
    // Here, speed is how OFTEN Step() is called (see IntervalFor), never how far it moves. A
    // late tick still advances exactly one colour. The walk slows for that instant; it cannot
    // gap. The cost is that a loop may take slightly longer than nominal - which nobody can
    // see, unlike a skipped colour.
    public sealed class RainbowWalker
    {
        // At 8ms a Windows DispatcherTimer is already at the edge of what it can honour, and
        // asking for less just makes it late - which now costs speed rather than colours.
        private static readonly TimeSpan FastestStep = TimeSpan.FromMilliseconds(8);

        private readonly ColourRamp _ramp;
        private int _index;

        public RainbowWalker(RainbowStyle style) => _ramp = ColourRamp.For(style);

        public void Reset() => _index = 0;

        // Returns where we are, then advances one. Returning first means the very first
        // colour of the ramp is shown rather than stepped over.
        public (byte R, byte G, byte B) Step()
        {
            var colour = _ramp[_index];
            _index = (_index + 1) % _ramp.Count;
            return colour;
        }

        // Speed expressed as a tick period. A zero or negative speed would throw when
        // assigned to DispatcherTimer.Interval, inside a tick, taking the app down - so it
        // clamps to the fastest step instead.
        public static TimeSpan IntervalFor(double coloursPerSecond) =>
            coloursPerSecond <= 0 ? FastestStep
                                  : TimeSpan.FromSeconds(1.0 / coloursPerSecond);

        // How long a full loop takes at this speed, nominally. An OUTPUT, not a setting: it
        // falls out of how many colours the style has. Asking for a shorter loop than the
        // ramp allows would mean skipping colours, which is what this class exists to stop.
        public double CycleSeconds(double coloursPerSecond) =>
            coloursPerSecond <= 0 ? double.PositiveInfinity : _ramp.Count / coloursPerSecond;
    }
}
```

- [ ] **Step 4: Link it into the test project**

Add to the same `<ItemGroup>`:

```xml
    <Compile Include="..\HidusbfModernGui\RainbowWalker.cs" Link="RainbowWalker.cs" />
```

- [ ] **Step 5: Run the tests**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 238` (224 + 14 new). Any other number, stop and report.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/RainbowWalker.cs HidusbfModernGui.Tests/RainbowWalkerTests.cs HidusbfModernGui.Tests/HidusbfModernGui.Tests.csproj
git commit -m "feat: a walker that advances one colour at a time

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Walk in the tick, and say what the speed means

**Files:**
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `RainbowWalker`, `ColourRamp` (Tasks 1-2)
- Produces: `RainbowSpeed` now means colours/second; `RainbowSpeedText` states the resulting cycle

**The shape of the change:** the tick stops asking "what time is it" and just steps. Speed
moves from the tick's *body* (how far to jump) to the tick's *period* (how often to fire).

- [ ] **Step 1: Add the walker, and retire the clock**

`MainWindow.xaml.cs:268` holds `private readonly Stopwatch _rainbowClock = new Stopwatch();`.
Its only uses are `_rainbowClock.Restart()` (line 515), `_rainbowClock.Stop()` (line 525) and
the `ColourAt` call in the tick (line 534). **Delete the field and all three uses** — a walker
that consulted a clock would be the old bug wearing a new name. Replace the field with:

```csharp
        private RainbowWalker? _rainbowWalker;
```

- [ ] **Step 2: The speed sets the tick's period, and every tick is +1**

Replace `Rainbow_Toggled` (line 511) entirely:

```csharp
        private void Rainbow_Toggled(object sender, RoutedEventArgs e)
        {
            if (RainbowCheck.IsChecked == true)
            {
                _rainbowWalker = new RainbowWalker(CurrentRainbowStyle);

                // Render priority, not the default Background. Background sits below Render
                // and Input, so UI work starved the tick - and the old time-driven colour
                // answered a late tick by jumping (measured: 1 dropped tick doubled the step,
                // 3 made it 7). The walker cannot jump, but a starved tick still costs speed,
                // so the priority still matters.
                //
                // The interval IS the speed: one colour per tick, so firing more often is the
                // only way to go faster. Apply() measures 1.0 ms, so even the 120/s ceiling is
                // ~12% of one core.
                _rainbowTimer ??= new DispatcherTimer(DispatcherPriority.Render);
                _rainbowTimer.Interval = RainbowWalker.IntervalFor(RainbowSpeed.Value);
                _rainbowTimer.Tick -= Rainbow_Tick;
                _rainbowTimer.Tick += Rainbow_Tick;
                _rainbowTimer.Start();
                LogStatus("Rainbow activo. Se detiene al cerrar la app.");
            }
            else
            {
                _rainbowTimer?.Stop();
            }
        }
```

Replace `Rainbow_Tick` (line 529) entirely:

```csharp
        private void Rainbow_Tick(object? sender, EventArgs e)
        {
            if (PlayStationList.SelectedItem is not UsbDeviceModel model) return;

            // Rebuilt lazily because a style change drops it: each style has its own ramp.
            _rainbowWalker ??= new RainbowWalker(CurrentRainbowStyle);

            // No clock, no elapsed time, no arithmetic. One tick, one colour.
            var (r, g, b) = _rainbowWalker.Step();

            // The picker follows the effect so the UI shows what the pad is doing. _updatingLight
            // stops that from bouncing back through Picker_ColorChanged and killing the effect on
            // its first tick.
            _updatingLight = true;
            try
            {
                Picker.SelectedColor = Color.FromRgb(r, g, b);
                UpdateSwatch();
            }
            finally { _updatingLight = false; }

            var state = new LightState(r, g, b,
                (PlayerLeds)((ComboBoxItem)PlayerLedList.SelectedItem).Tag,
                (LedBrightness)((ComboBoxItem)BrightnessList.SelectedItem).Tag);
            DualSenseLight.Apply(model.InstanceId, state);
        }
```

Replace `RainbowStyle_Changed` (line 504) entirely:

```csharp
        private void RainbowStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateRainbowHint();

            // Each style has its own ramp, so the walker is dropped and the tick rebuilds it.
            // No write here: the tick picks it up on its own, and writing too would race it.
            _rainbowWalker = null;
            UpdateRainbowSpeedText();
        }
```

`DispatcherPriority` and `DispatcherTimer` are in `System.Windows.Threading`, already imported.
Once `_rainbowClock` is gone, `using System.Diagnostics;` may become unused — remove it only if
the compiler warns, since other code may rely on it.

- [ ] **Step 3: The speed slider becomes colours per second**

In `MainWindow.xaml`, replace the `RainbowSpeed` slider and the `RainbowSpeedText` beside it
(lines 551-552), keeping the surrounding `VELOCIDAD` label as it is:

```xml
                                                <!-- Colours per second, not cycle seconds. One colour per tick means
                                                     this sets how OFTEN the timer fires, so the cycle length is
                                                     reported rather than requested. 120/s is an 8ms tick - about as
                                                     fast as a DispatcherTimer can honestly be asked to fire. -->
                                                <Slider x:Name="RainbowSpeed" Minimum="5" Maximum="120" Value="30" Width="130"
                                                        VerticalAlignment="Center" ValueChanged="RainbowSpeed_Changed"/>
                                                <TextBlock x:Name="RainbowSpeedText" Text="" Style="{StaticResource DataText}"
                                                           VerticalAlignment="Center" Margin="8,0,0,0"/>
```

- [ ] **Step 4: Retune the running timer, and report the cycle**

```csharp
        // Speed is the tick's period, so a drag has to retune the live timer - there is no
        // longer a speed term inside the tick that would pick the change up on its own.
        private void RainbowSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_rainbowTimer != null)
                _rainbowTimer.Interval = RainbowWalker.IntervalFor(RainbowSpeed.Value);
            UpdateRainbowSpeedText();
        }

        // The cycle time is derived, so it is stated rather than asked for. A user who picked
        // a cycle length would be picking a number the walk cannot honour without skipping
        // colours - which is the defect this exists to remove.
        private void UpdateRainbowSpeedText()
        {
            if (RainbowSpeedText == null || RainbowSpeed == null) return;

            double cycle = new RainbowWalker(CurrentRainbowStyle).CycleSeconds(RainbowSpeed.Value);
            RainbowSpeedText.Text = $"{RainbowSpeed.Value:0}/s · vuelta {cycle:0.#} s";
        }
```

Constructing a `RainbowWalker` here is free — `ColourRamp.For` is cached, so it is a dictionary
lookup, not a rebuild.

Call `UpdateRainbowSpeedText()` at the end of `BuildLightControls()`, inside the existing `try`.

- [ ] **Step 5: Verify**

Run: `cd HidusbfModernGui && dotnet build`
Expected: `Build succeeded.`

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: `Passed: 238`.

Run the anti-blue gate:
```bash
grep -inE "6366F1|818CF8|0F172A|1E293B|312E81|EEF2FF|E6E9F2|AccentIndigo|SidebarBg|PanelBg|WindowBg|CardDark|InputBg|TextPrimary|TextSecondary" HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs HidusbfModernGui/Theme.xaml
```
Expected: empty.

- [ ] **Step 6: Commit**

```bash
git add HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "feat: the rainbow walks its ramp instead of chasing the clock

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Verify — and the controller is the only judge

**Files:** none

- [ ] **Step 1: Tests and build**

Run: `cd HidusbfModernGui.Tests && dotnet test` → `Passed: 238`
Run: `cd HidusbfModernGui && dotnet build` → succeeds

- [ ] **Step 2: Backend untouched**

Run: `cd VerifyState && dotnet run`
Expected: `Build (by hash) : NoPatch`, `ModeText : No Patch`, a non-zero device count. Do not assert an exact count — it is environmental.

- [ ] **Step 3: Push the slider to 120/s and see whether the tick can honour it**

At the top of the slider the timer is asked to fire every 8 ms and write a HID report each time. `Apply` measures 1.0 ms, so the arithmetic says ~12% of a core — but arithmetic said the old rainbow was smooth too, and the user's eyes said otherwise.

Two things to watch, and report both:
- **Does the UI stay responsive?** Drag the window, click through the device list. If it goes sticky, say so.
- **Does 120/s actually run at 120/s?** A DispatcherTimer asked for 8 ms may deliver 15. That no longer skips colours — the walk just runs slower than the label claims — but the label would then be lying. If the loop visibly takes longer than the stated `vuelta`, report it: the honest fix is lowering the slider's maximum to a rate the timer can hold, not leaving a number that overpromises.

- [ ] **Step 4: Binding errors**

Check the debug output for `System.Windows.Data Error`. Expected: none.

- [ ] **Step 5: Watch the controller — this is what the plan is for**

Nothing above proves the thing the user asked for. Only this does.

- **Rainbow on, Suave, 30/s.** The lightbar should glide. No sense of stepping between red, orange, yellow.
- **Drop the speed to 5/s.** It should crawl through every shade. This is the strongest test: at 5 colours/second the walk is slow enough to see individual colours, and if there is a gap it will be obvious.
- **Try Equilibrado.** It used to jump 17 in a single frame (`#13AD00 → #02AE00`); walking the ramp should have removed that entirely. If it still jumps there, the ramp is not being walked and something in Task 3 is wrong.
- **Watch the picker's hue cursor** while the effect runs. It should slide, not hop.
- **Leave it running a few minutes.** The walk should not drift, stall, or snap at the loop seam.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore: verify the stepwise rainbow end to end

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Only offer speeds the timer actually keeps

**Added after Task 4's measurement disproved Task 3's slider.**

Task 3 shipped `Minimum="5" Maximum="120"` colours/second and a label computing the cycle from
the *requested* speed. Measured against a real `DispatcherTimer` at `Render` priority, that
label is false at almost every position:

```
  pedido   intervalo    real     
   30/s     33.3 ms    21.4/s   <- the DEFAULT, off by 29%
   60/s     16.7 ms    40.0/s
  120/s      8.3 ms    64.0/s   <- off by 47%
```

The cause: Windows delivers timer ticks on a **15.625 ms** cadence and no finer. An interval
between ticks does not run faster — it rounds **up** to the next tick and runs slow. So the only
speeds the timer keeps are `1000 / (n × 15.625)`:

```
   n=1  15.6 ms  64.0/s      n=4   62.5 ms  16.0/s      n=8  125.0 ms   8.0/s
   n=2  31.2 ms  32.0/s      n=5   78.1 ms  12.8/s      n=10 156.3 ms   6.4/s
   n=3  46.9 ms  21.3/s      n=6   93.8 ms  10.7/s      n=12 187.5 ms   5.3/s
```

Measured at these values, delivery is exact. **`timeBeginPeriod(1)` does not help** — tested, the
floor stays 15.625 ms, so raising the system timer resolution is not a fix worth its power cost.

This does not threaten the feature: 64 colours/s is far faster than anyone wants a lightbar to
run (Smooth's 681-colour ramp completes in 10.6 s at n=1), and the user's complaint was that the
effect was too abrupt, not too slow. The ceiling is comfortable. **The lying label is the defect.**

So the slider stops offering speeds that do not exist. It selects `n` — whole timer ticks per
colour — and every position is one the timer honours.

**Files:**
- Modify: `HidusbfModernGui/RainbowWalker.cs`
- Modify: `HidusbfModernGui.Tests/RainbowWalkerTests.cs`
- Modify: `HidusbfModernGui/MainWindow.xaml`
- Modify: `HidusbfModernGui/MainWindow.xaml.cs`

**Interfaces:**
- Replaces `RainbowWalker.IntervalFor(double coloursPerSecond)` and `CycleSeconds(double coloursPerSecond)` with:
  - `const double RainbowWalker.OsTickMs = 15.625`
  - `static TimeSpan IntervalFor(int ticksPerColour)` — clamped to `[1, 12]`
  - `static double ColoursPerSecond(int ticksPerColour)`
  - `double CycleSeconds(int ticksPerColour)`

`Step()` does not change. Nothing here puts a clock in the walk — it only stops the UI promising
a rate the timer cannot deliver.

- [ ] **Step 1: Write the failing tests**

Replace the `IntervalFor` and `CycleSeconds` tests in `HidusbfModernGui.Tests/RainbowWalkerTests.cs`
(`IntervalFor_TurnsSpeedIntoATickPeriod`, `IntervalFor_NonsenseSpeed_IsClampedRatherThanThrowing`,
`CycleSeconds_FallsOutOfTheRampLength`, `CycleSeconds_NegativeSpeed_IsClampedToInfinite`) with:

```csharp
        // Windows delivers timer ticks on a 15.625ms cadence and no finer - measured against a
        // real DispatcherTimer, and timeBeginPeriod(1) does not move it. So an interval must be
        // a whole number of ticks, or it rounds up and the walk runs slower than the UI claims.
        [Theory]
        [InlineData(1, 15.625)]
        [InlineData(2, 31.25)]
        [InlineData(3, 46.875)]
        [InlineData(12, 187.5)]
        public void IntervalFor_IsAWholeNumberOfTimerTicks(int ticksPerColour, double expectedMs)
        {
            Assert.Equal(expectedMs, RainbowWalker.IntervalFor(ticksPerColour).TotalMilliseconds, 3);
        }

        // Measured: n=1 delivers 64/s, n=2 delivers 32/s, n=3 delivers 21.3/s. These are the
        // numbers the UI shows the user, so they must be the numbers the timer actually keeps.
        [Theory]
        [InlineData(1, 64.0)]
        [InlineData(2, 32.0)]
        [InlineData(3, 21.33)]
        [InlineData(12, 5.33)]
        public void ColoursPerSecond_MatchesWhatTheTimerDelivers(int ticksPerColour, double expected)
        {
            Assert.Equal(expected, RainbowWalker.ColoursPerSecond(ticksPerColour), 2);
        }

        // Feeds DispatcherTimer.Interval, which throws on zero or negative - inside a tick, that
        // takes the app down. The slider cannot produce these, but a profile or a later edit could.
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(999)]
        public void IntervalFor_OutOfRange_IsClampedToSomethingUsable(int ticksPerColour)
        {
            var interval = RainbowWalker.IntervalFor(ticksPerColour);
            Assert.InRange(interval.TotalMilliseconds, 15.625, 187.5);
        }

        // The cycle time is an OUTPUT: it falls out of the ramp length and the real rate.
        // Smooth has 681 colours, so at n=1 (64/s) a lap is 10.6s.
        [Fact]
        public void CycleSeconds_FallsOutOfTheRampLengthAndTheRealRate()
        {
            var walker = new RainbowWalker(RainbowStyle.Smooth);
            int count = ColourRamp.For(RainbowStyle.Smooth).Count;

            Assert.Equal(count / 64.0, walker.CycleSeconds(1), 2);
            Assert.Equal(count / 32.0, walker.CycleSeconds(2), 2);
        }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd HidusbfModernGui.Tests && dotnet test`
Expected: FAIL to compile — `IntervalFor` takes a `double`, and `ColoursPerSecond` does not exist.

- [ ] **Step 3: Implement**

In `HidusbfModernGui/RainbowWalker.cs`, replace `FastestStep`, `IntervalFor` and `CycleSeconds`:

```csharp
        // Windows delivers DispatcherTimer ticks on a 15.625ms cadence and no finer. This is
        // measured, not assumed: asking for 8.3ms (120/s) delivered 15.6ms (64/s), and asking
        // for 33.3ms (30/s) delivered 46.9ms (21.4/s) - it rounds UP to the next tick, so an
        // in-between interval does not run faster, it runs slow while the UI claims otherwise.
        // timeBeginPeriod(1) was tried and does not move this floor.
        //
        // So speed is counted in whole ticks per colour. Every value the slider can produce is
        // one the timer actually keeps, and the cycle length the UI reports is true.
        public const double OsTickMs = 15.625;

        private const int FastestTicksPerColour = 1;    // 64 colours/s
        private const int SlowestTicksPerColour = 12;   // 5.3 colours/s

        // Clamped rather than validated: this feeds DispatcherTimer.Interval from inside a tick,
        // where a throw takes the app down.
        public static TimeSpan IntervalFor(int ticksPerColour) =>
            TimeSpan.FromMilliseconds(Clamp(ticksPerColour) * OsTickMs);

        public static double ColoursPerSecond(int ticksPerColour) =>
            1000.0 / (Clamp(ticksPerColour) * OsTickMs);

        // How long a full loop takes, from the ramp length and the rate the timer really keeps.
        // An OUTPUT, not a setting: a user who picked a lap length would be picking a number the
        // walk can only honour by skipping colours, which is what this class exists to stop.
        public double CycleSeconds(int ticksPerColour) =>
            _ramp.Count / ColoursPerSecond(ticksPerColour);

        private static int Clamp(int ticksPerColour) =>
            Math.Clamp(ticksPerColour, FastestTicksPerColour, SlowestTicksPerColour);
```

- [ ] **Step 4: The slider selects whole ticks**

In `HidusbfModernGui/MainWindow.xaml`, replace the `RainbowSpeed` slider:

```xml
                                                <!-- Whole timer ticks per colour, reversed so right = faster. The
                                                     timer only honours multiples of 15.625ms (measured), so a
                                                     continuous speed slider would promise rates it cannot keep:
                                                     the old 30/s default really ran at 21.4/s. Every stop here is
                                                     a rate the timer actually delivers. -->
                                                <Slider x:Name="RainbowSpeed" Minimum="1" Maximum="12" Value="3" Width="130"
                                                        IsDirectionReversed="True" IsSnapToTickEnabled="True" TickFrequency="1"
                                                        VerticalAlignment="Center" ValueChanged="RainbowSpeed_Changed"/>
```

- [ ] **Step 5: Report the real rate**

In `HidusbfModernGui/MainWindow.xaml.cs`, update the two call sites and the text:

```csharp
        private int TicksPerColour => (int)RainbowSpeed.Value;
```

In `Rainbow_Toggled` and `RainbowSpeed_Changed`, replace `RainbowWalker.IntervalFor(RainbowSpeed.Value)`
with `RainbowWalker.IntervalFor(TicksPerColour)`. Then:

```csharp
        // Both numbers are what the timer really delivers, not what was requested - the whole
        // point of counting in ticks. A label that overpromises is the defect this fixes.
        private void UpdateRainbowSpeedText()
        {
            if (RainbowSpeedText == null || RainbowSpeed == null) return;

            var walker = new RainbowWalker(CurrentRainbowStyle);
            RainbowSpeedText.Text = $"{RainbowWalker.ColoursPerSecond(TicksPerColour):0.#}/s · vuelta {walker.CycleSeconds(TicksPerColour):0.#} s";
        }
```

- [ ] **Step 6: Verify**

Run: `cd HidusbfModernGui.Tests && dotnet test` → expect 240 passing (238, minus 5 replaced tests, plus 7 new). **Report the real number.**
Run: `cd HidusbfModernGui && dotnet build` → `Build succeeded`
Run the anti-blue gate → empty.

- [ ] **Step 7: Commit**

```bash
git add HidusbfModernGui/RainbowWalker.cs HidusbfModernGui.Tests/RainbowWalkerTests.cs HidusbfModernGui/MainWindow.xaml HidusbfModernGui/MainWindow.xaml.cs
git commit -m "fix: only offer rainbow speeds the timer actually keeps

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Notes for the implementer

**Do not change `RainbowEffect.ColourAt`.** The ramp is built from it, and its 23 tests still guard it. It stops being what the tick calls; it does not stop existing.

**Do not raise `ColourRamp.Samples` to make a test pass.** If `EveryStepIsAtMostOne` fails, the failure message names the exact two colours and the gap. That is a fact about the style worth reporting, not a number to tune until it goes quiet.

**The cycle time is an output.** Every previous version of this feature let the user request a cycle length, and honoured it by skipping colours. That is the bug. The speed slider sets colours per second; the cycle is whatever that implies, and the UI says so.

**Task 5 exists because Task 4's measurement contradicted Task 3.** See its section — do not treat the `Minimum="5" Maximum="120"` slider from Task 3 as settled.

**`Step()` must never learn what time it is.** If you find yourself wanting to pass it elapsed seconds, or to catch up after a slow tick by stepping twice, stop — that is the original bug being rebuilt. A late tick means the walk runs a fraction slower. That is the trade this plan makes deliberately, and it is the right one: nobody can see a loop that took 31.4 s instead of 30.0 s, and everybody can see a skipped colour.
