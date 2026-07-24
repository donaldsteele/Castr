using Castr.Core.Chunking;

namespace Castr.Core.Tests.Chunking;

public class FileSystemFileSourceSinkTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("castr-core-tests-").FullName;

    [Fact]
    public async Task FileSystemFileSource_ReadsBackExactBytesWritten()
    {
        var data = new byte[10_000];
        new Random(42).NextBytes(data);
        var path = Path.Combine(_tempDir, "source.bin");
        await File.WriteAllBytesAsync(path, data);

        using var source = new FileSystemFileSource(path);
        Assert.Equal(data.Length, source.Length);

        var buffer = new byte[data.Length];
        int read = await source.ReadAsync(0, buffer);

        Assert.Equal(data.Length, read);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public async Task FileSystemFileSource_ReadsPartialRangeAtOffset()
    {
        var data = "the quick brown fox jumps over the lazy dog"u8.ToArray();
        var path = Path.Combine(_tempDir, "source.bin");
        await File.WriteAllBytesAsync(path, data);

        using var source = new FileSystemFileSource(path);
        var buffer = new byte[5];
        int read = await source.ReadAsync(4, buffer);

        Assert.Equal(5, read);
        Assert.Equal("quick"u8.ToArray(), buffer);
    }

    [Fact]
    public async Task FileSystemFileSink_WritesToPartFileUntilCompleted()
    {
        var finalPath = Path.Combine(_tempDir, "output.bin");
        var partPath = finalPath + ".part";
        var data = "castr chunk contents"u8.ToArray();

        using (var sink = new FileSystemFileSink(finalPath, data.Length))
        {
            await sink.WriteAsync(0, data);
            Assert.True(File.Exists(partPath));
            Assert.False(File.Exists(finalPath));

            sink.Complete();
        }

        Assert.False(File.Exists(partPath));
        Assert.True(File.Exists(finalPath));
        Assert.Equal(data, await File.ReadAllBytesAsync(finalPath));
    }

    [Fact]
    public async Task FileSystemFileSink_WritesOutOfOrderChunksToCorrectOffsets()
    {
        var finalPath = Path.Combine(_tempDir, "output.bin");
        using var sink = new FileSystemFileSink(finalPath, 30);

        await sink.WriteAsync(20, "chunk-three-here!!!!"u8.ToArray());
        await sink.WriteAsync(0, "chunk-one-!!"u8.ToArray());
        await sink.WriteAsync(12, "chunk-two!!"u8.ToArray());

        sink.Complete();

        var result = await File.ReadAllBytesAsync(finalPath);
        Assert.Equal("chunk-one-!!"u8.ToArray(), result[0..12]);
        Assert.Equal("chunk-two!!"u8.ToArray(), result[12..23]);
    }

    [Fact]
    public async Task RoundTrip_ChunkAndReassemble_ProducesIdenticalFile()
    {
        var original = new byte[50_000];
        new Random(7).NextBytes(original);
        var sourcePath = Path.Combine(_tempDir, "original.bin");
        await File.WriteAllBytesAsync(sourcePath, original);

        const int chunkSize = 4096;
        using var source = new FileSystemFileSource(sourcePath);
        var destPath = Path.Combine(_tempDir, "reassembled.bin");
        using var sink = new FileSystemFileSink(destPath, source.Length);

        foreach (var range in ChunkLayout.EnumerateRanges(source.Length, chunkSize))
        {
            var chunk = await Chunker.ReadChunkAsync(source, chunkSize, range.Index);
            await sink.WriteAsync(range.Offset, chunk);
        }
        sink.Complete();

        Assert.Equal(original, await File.ReadAllBytesAsync(destPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort cleanup */ }
    }
}
