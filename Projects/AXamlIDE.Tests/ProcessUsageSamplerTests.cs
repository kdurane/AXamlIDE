using System;
using AXamlIDE.Services.Debugging;

namespace AXamlIDE.Tests;

public class ProcessUsageSamplerTests
{
    [Theory]
    [InlineData(1.0, 1.0, 1, 100)]     // one processor, busy the whole second
    [InlineData(1.0, 1.0, 4, 25)]      // one of four processors busy
    [InlineData(0.5, 2.0, 1, 25)]
    [InlineData(8.0, 1.0, 4, 100)]     // more CPU than wall time (rounding between readings) stays at 100
    [InlineData(1.0, 0.0, 4, 0)]       // no time passed
    public void CpuPercentIsShareOfAllProcessors(double cpuSeconds, double elapsedSeconds, int processors, double expected)
        => Assert.Equal(expected, ProcessUsageSampler.CpuPercent(
            TimeSpan.FromSeconds(cpuSeconds), TimeSpan.FromSeconds(elapsedSeconds), processors), precision: 6);

    [Fact]
    public void ReadsARunningProcess()
    {
        using var sampler = new ProcessUsageSampler(Environment.ProcessId);

        var first = sampler.Sample();
        var second = sampler.Sample();

        Assert.NotNull(first);
        Assert.True(first.Value.MemoryBytes > 0);
        Assert.Null(first.Value.CpuPercent);      // nothing to compare the first reading with
        Assert.NotNull(second?.CpuPercent);
        Assert.InRange(second.Value.CpuPercent!.Value, 0, 100);
    }

    [Fact]
    public void NoSuchProcessThrowsArgumentException()
        => Assert.Throws<ArgumentException>(() => new ProcessUsageSampler(int.MaxValue));
}
