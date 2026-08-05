namespace Procexp.App;

/// <summary>
/// A rolling average over the last few samples, for the status bar's timing
/// readouts. Ten samples is enough to smooth jitter without hiding a change.
/// </summary>
internal sealed class RollingAverage
{
    private readonly Queue<double> _samples = new();

    public void Record(double value)
    {
        _samples.Enqueue(value);
        while (_samples.Count > 10)
        {
            _samples.Dequeue();
        }
    }

    public double Average => _samples.Count == 0 ? 0 : _samples.Average();
}
