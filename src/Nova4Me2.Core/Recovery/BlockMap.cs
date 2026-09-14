namespace Nova4Me2.Core.Recovery;

public enum BlockState : byte { Pending, Reading, Good, Bad, Skipped, Slow }

/// <summary>Coarse visual map of a byte range: N cells, each summarising a slice of the range (for the UI's animated disk view).</summary>
public sealed class BlockMap
{
    public long Start { get; }
    public long Length { get; }
    public int Cells { get; }
    public BlockState[] States { get; }
    public long CellBytes { get; }

    public BlockMap(long start, long length, int cells = 1024)
    {
        Start = start; Length = Math.Max(1, length); Cells = Math.Max(1, cells);
        CellBytes = Math.Max(1, (Length + Cells - 1) / Cells);
        States = new BlockState[Cells];
    }

    public int CellOf(long offset) => (int)Math.Min(Cells - 1, Math.Max(0, (offset - Start) / CellBytes));

    /// <summary>Mark a byte range; a "worse" state never gets downgraded by a better one (Bad beats Good).</summary>
    public void Mark(long offset, long length, BlockState state)
    {
        int a = CellOf(offset), b = CellOf(Math.Max(offset, offset + length - 1));
        for (int i = a; i <= b; i++)
        {
            var cur = States[i];
            if (Rank(state) >= Rank(cur) || state == BlockState.Reading && cur == BlockState.Pending) States[i] = state;
        }
    }

    private static int Rank(BlockState s) => s switch { BlockState.Bad => 5, BlockState.Skipped => 4, BlockState.Slow => 3, BlockState.Good => 2, BlockState.Reading => 1, _ => 0 };

    public (int good, int bad, int skipped, int slow, int pending) Summary()
    {
        int g = 0, b = 0, sk = 0, sl = 0, p = 0;
        foreach (var s in States) switch (s) { case BlockState.Good: g++; break; case BlockState.Bad: b++; break; case BlockState.Skipped: sk++; break; case BlockState.Slow: sl++; break; default: p++; break; }
        return (g, b, sk, sl, p);
    }

    public BlockMap Clone()
    {
        var c = new BlockMap(Start, Length, Cells);
        Array.Copy(States, c.States, Cells);
        return c;
    }
}
