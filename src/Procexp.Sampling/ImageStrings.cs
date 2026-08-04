using System.Text;

namespace Procexp.Sampling;

/// <summary>
/// Printable string extraction for the Strings tab of the Properties window —
/// the equivalent of running <c>strings</c> over the image.
/// </summary>
internal static class ImageStrings
{
    private const int MinimumRunLength = 4;
    private const int MaximumResults = 20_000;
    private const long MaximumFileBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Extract printable ASCII runs of at least four characters.
    /// </summary>
    /// <remarks>
    /// Capped in both directions: large images are truncated, and the result list
    /// is bounded, because a stripped-but-large binary can otherwise yield
    /// hundreds of thousands of runs and stall the UI thread that renders them.
    /// </remarks>
    internal static IReadOnlyList<string> Extract(string path)
    {
        var results = new List<string>(1024);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return results;
        }

        using (stream)
        {
            var builder = new StringBuilder(256);
            var buffer = new byte[65536];
            long consumed = 0;

            while (consumed < MaximumFileBytes && results.Count < MaximumResults)
            {
                var read = stream.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                consumed += read;

                for (var i = 0; i < read; i++)
                {
                    var b = buffer[i];
                    if (b is >= 0x20 and < 0x7F)
                    {
                        builder.Append((char)b);
                    }
                    else
                    {
                        if (builder.Length >= MinimumRunLength)
                        {
                            results.Add(builder.ToString());
                            if (results.Count >= MaximumResults)
                            {
                                break;
                            }
                        }

                        builder.Clear();
                    }
                }
            }

            if (builder.Length >= MinimumRunLength && results.Count < MaximumResults)
            {
                results.Add(builder.ToString());
            }
        }

        return results;
    }
}
