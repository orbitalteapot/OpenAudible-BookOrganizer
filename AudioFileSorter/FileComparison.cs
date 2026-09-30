using System.Buffers;

namespace AudioFileSorter;

/// <summary>
/// Whether two files hold the same book. The update check uses it to decide whether a copy is
/// current, and the planner to tell whose an old file in the destination is, so both judge a file
/// by the same rule.
/// </summary>
internal static class FileComparison
{
    private const int SampleSize = 4096;
    private const int ComparisonBufferSize = 131072;

    /// <summary>
    /// Cheap "is this the same file" check. Comparing every byte of a multi-gigabyte library on
    /// every run is not viable, so size plus three sampled chunks is used instead. It reads a few
    /// kilobytes, so it is synchronous: the planner runs it while it decides where books go.
    /// </summary>
    internal static bool AreSameQuick(string filePath1, string filePath2)
    {
        try
        {
            var fileInfo1 = new FileInfo(filePath1);
            var fileInfo2 = new FileInfo(filePath2);

            if (!fileInfo1.Exists || !fileInfo2.Exists || fileInfo1.Length != fileInfo2.Length)
            {
                return false;
            }

            var length = fileInfo1.Length;
            if (length == 0)
            {
                return true;
            }

            var buffer1 = new byte[SampleSize];
            var buffer2 = new byte[SampleSize];

            using var stream1 = new FileStream(filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, SampleSize);
            using var stream2 = new FileStream(filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, SampleSize);

            foreach (var offset in GetSampleOffsets(length))
            {
                stream1.Seek(offset, SeekOrigin.Begin);
                stream2.Seek(offset, SeekOrigin.Begin);

                var read1 = stream1.ReadAtLeast(buffer1, SampleSize, throwOnEndOfStream: false);
                var read2 = stream2.ReadAtLeast(buffer2, SampleSize, throwOnEndOfStream: false);

                if (read1 != read2 || !buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // Assume the files differ: a copy is retried, and an old file is not taken for the book.
        }
    }

    /// <summary>
    /// Byte-for-byte comparison of two files. Used by <see cref="Model.FileComparisonMode.Full"/>, where
    /// the point is to notice a re-released book that happens to be exactly the same size as the
    /// copy already on disk — something the sampled check cannot see.
    ///
    /// Internal rather than private so the "cancel stops it promptly" guarantee can be tested
    /// directly: this is the only unbounded loop in a sort, and on a large library it is where a
    /// cancelled run would otherwise keep grinding.
    /// </summary>
    internal static async Task<bool> AreIdenticalAsync(string filePath1, string filePath2, CancellationToken cancellationToken)
    {
        byte[]? buffer1 = null;
        byte[]? buffer2 = null;

        try
        {
            var fileInfo1 = new FileInfo(filePath1);
            var fileInfo2 = new FileInfo(filePath2);

            if (!fileInfo1.Exists || !fileInfo2.Exists || fileInfo1.Length != fileInfo2.Length)
            {
                return false;
            }

            if (fileInfo1.Length == 0)
            {
                return true;
            }

            buffer1 = ArrayPool<byte>.Shared.Rent(ComparisonBufferSize);
            buffer2 = ArrayPool<byte>.Shared.Rent(ComparisonBufferSize);

            await using var stream1 = new FileStream(
                filePath1, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ComparisonBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var stream2 = new FileStream(
                filePath2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ComparisonBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                // Checked here as well as passed to the reads: a whole-file comparison of a large
                // book is many iterations long, and cancellation must not have to wait for the
                // reads to notice it.
                cancellationToken.ThrowIfCancellationRequested();

                var read1 = await stream1.ReadAtLeastAsync(
                    buffer1.AsMemory(0, ComparisonBufferSize), ComparisonBufferSize, throwOnEndOfStream: false, cancellationToken);
                var read2 = await stream2.ReadAtLeastAsync(
                    buffer2.AsMemory(0, ComparisonBufferSize), ComparisonBufferSize, throwOnEndOfStream: false, cancellationToken);

                if (read1 != read2)
                {
                    // The lengths matched a moment ago, so one of the files is being written to
                    // right now. Treat it as different and copy again on this or the next run.
                    return false;
                }

                if (read1 == 0)
                {
                    return true;
                }

                if (!buffer1.AsSpan(0, read1).SequenceEqual(buffer2.AsSpan(0, read2)))
                {
                    return false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false; // Assume the files differ so the copy is retried.
        }
        finally
        {
            if (buffer1 is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer1);
            }

            if (buffer2 is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer2);
            }
        }
    }

    private static IEnumerable<long> GetSampleOffsets(long length)
    {
        yield return 0;

        if (length <= SampleSize)
        {
            yield break;
        }

        var middle = Math.Max(0, (length / 2) - (SampleSize / 2));
        if (middle > 0)
        {
            yield return middle;
        }

        yield return Math.Max(0, length - SampleSize);
    }
}
