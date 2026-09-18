using HlslPerf.Crossover;
using Xunit;

namespace HlslPerf.Core.Tests;

public sealed class CrossoverModelTests
{
    [Fact] public void PopulationIsRepeatableAndPrefixStable()
    {
        var a = Model.Generate(100, 69501203); var b = Model.Generate(200, 69501203);
        Assert.Equal(28, System.Runtime.InteropServices.Marshal.SizeOf<Agent>());
        Assert.Equal(a, b.Take(100));
        Assert.NotEqual(a[0], Model.Generate(100, 7)[0]);
        foreach (var p in a) { Assert.InRange(p.x, -120, 120); Assert.InRange(p.y, -68, 68); Assert.InRange(p.size, .06f, .16f); }
    }
    [Fact] public void PredicateIncludesBoundaryAndRejectsEitherOutsideAxis()
    {
        var v = new View { x = 3, y = -2, halfX = 4, halfY = 5 };
        Assert.True(Model.Visible(new Agent { x = 8, y = 4, size = 1 }, v));
        Assert.True(Model.Visible(new Agent { x = -2, y = -8, size = 1 }, v));
        Assert.False(Model.Visible(new Agent { x = 8.001f, y = -2, size = 1 }, v));
        Assert.False(Model.Visible(new Agent { x = 3, y = -8.001f, size = 1 }, v));
        Assert.False(Model.Visible(new Agent { x = float.NaN, y = -2, size = 1 }, v));
    }
    [Fact] public void CullMatchesIndependentOracleAndWritesOnlyVisiblePrefix()
    {
        var a = Model.Generate(10000, 8); var v = Model.At(777, .6f);
        var actual = Enumerable.Repeat(uint.MaxValue, a.Length).ToArray();
        int n = Model.Cull(a, v, actual);
        var expected = Enumerable.Range(0, a.Length).Where(i => a[i].x >= v.x - (v.halfX + a[i].size) &&
            a[i].x <= v.x + (v.halfX + a[i].size) && a[i].y >= v.y - (v.halfY + a[i].size) &&
            a[i].y <= v.y + (v.halfY + a[i].size)).Select(i => (uint)i).ToArray();
        Assert.Equal(expected, actual.Take(n)); Assert.All(actual.Skip(n), x => Assert.Equal(uint.MaxValue, x));
    }
    [Fact] public void SetComparisonAcceptsPermutationButRejectsDuplicateMissingOrForeignIds()
    {
        uint[] expected = { 1, 4, 9 };
        Assert.True(Model.EqualSets(expected, 3, new uint[] { 9, 1, 4 }));
        Assert.False(Model.EqualSets(expected, 3, new uint[] { 1, 4, 4 }));
        Assert.False(Model.EqualSets(expected, 3, new uint[] { 1, 4 }));
        Assert.False(Model.EqualSets(expected, 3, new uint[] { 1, 4, 10 }));
    }
    [Theory]
    [InlineData("--mode", "bogus")]
    [InlineData("--agents", "4000001")]
    [InlineData("--density", "NaN")]
    [InlineData("--density", "1")]
    [InlineData("--frames", "0")]
    [InlineData("--cpu-workers", "12")]
    [InlineData("--typo", "1")]
    public void InvalidArgumentsFail(string key, string value) => Assert.ThrowsAny<ArgumentException>(() =>
        Options.Parse(new[] { "--calibration", "frozen.json", key, value }));

    [Fact] public void ParserHandlesUnityFlagsAndRequiresFrozenCalibration()
    {
        var o = Options.Parse(new[] { "app.exe", "-batchmode", "-force-d3d12", "--mode", "cpu", "--calibration", "x.json", "--frames", "25" });
        Assert.Equal("cpu", o.mode); Assert.Equal(25, o.frames);
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--frames" }));
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--mode", "gpu" }));
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--mode", "cpu", "--mode", "gpu" }));
    }
    [Fact] public void CalibrationTracksRequestedDensityAndCheckFramesIncludeEndpoints()
    {
        var agents = Model.Generate(10000, 69501203); var ids = new uint[agents.Length];
        float scale = Model.Calibrate(agents, 1000, .25);
        double mean = Enumerable.Range(0, 32).Average(i => Model.Cull(agents, Model.At(i * 999 / 31, scale), ids) / 10000.0);
        Assert.InRange(mean, .248, .252);
        Assert.Equal(Model.At(0, scale), Model.At(0, scale));
        Assert.Equal(0, Model.CheckFrames(1000)[0]); Assert.Equal(999, Model.CheckFrames(1000)[^1]);
        Assert.Equal(10, Model.CheckFrames(1000).Distinct().Count());
    }
}
