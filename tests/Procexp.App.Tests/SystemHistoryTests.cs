using Procexp.App;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

public class SystemHistoryTests
{
    private static SystemHistory.Entry Entry(double cpu) =>
        new(SystemStats.Zero with { CpuTotalPercent = cpu }, $"proc — {cpu:F0}%", null);

    [Fact]
    public void Record_AppendsInOrderAndRaisesEvent()
    {
        var history = new SystemHistory();
        var raised = new List<SystemHistory.Entry>();
        history.Recorded += raised.Add;

        history.Record(Entry(1));
        history.Record(Entry(2));

        Assert.Equal(2, history.Entries.Count);
        Assert.Equal(1, history.Entries[0].Stats.CpuTotalPercent);
        Assert.Equal(2, history.Entries[1].Stats.CpuTotalPercent);
        Assert.Equal(2, raised.Count);
        Assert.Equal(2, raised[1].Stats.CpuTotalPercent);
    }

    [Fact]
    public void Record_TrimsOldestBeyondCapacity()
    {
        var history = new SystemHistory();

        for (var i = 0; i < SystemHistory.Capacity + 10; i++)
        {
            history.Record(Entry(i));
        }

        Assert.Equal(SystemHistory.Capacity, history.Entries.Count);
        Assert.Equal(10, history.Entries[0].Stats.CpuTotalPercent);
        Assert.Equal(SystemHistory.Capacity + 9, history.Entries[^1].Stats.CpuTotalPercent);
    }
}
