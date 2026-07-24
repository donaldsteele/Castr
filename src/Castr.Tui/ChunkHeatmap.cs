using Spectre.Console;
using Spectre.Console.Rendering;

namespace Castr.Tui;

/// <summary>
/// A custom <see cref="IRenderable"/> that paints the chunk space as a grid of colored blocks —
/// a "chunk-bitmap heatmap".
/// <para>
/// <b>Approximation note.</b> A pixel-accurate heatmap would color each individual chunk by its
/// received/missing state, but <see cref="Castr.Core.Protocol.TransferProgress"/> is aggregate-only: it
/// reports <c>CompletedChunks</c>/<c>TotalChunks</c> counts, not the raw <c>ChunkBitmap</c>. With only the
/// count available, this renderable divides the chunk space into fixed cells and fills them in order to show
/// completion <i>density</i> across the transfer: solid green for a fully-completed range, a half-lit yellow
/// cell for the range currently in flight, and dim grey for ranges not yet done. It faithfully reflects "how
/// much" is done and animates smoothly; it does not (and cannot, from this snapshot) show <i>which</i> exact
/// chunks are still missing. Wiring the real per-chunk bitmap through would need a new read-only accessor on
/// the sessions — deliberately out of scope here (Core is frozen).
/// </para>
/// </summary>
internal sealed class ChunkHeatmap(int completedChunks, int totalChunks, int maxCells = 256) : IRenderable
{
    private const char FullBlock = '█';   // █ completed
    private const char HalfBlock = '▒';   // ▒ in-flight / partially complete cell
    private const char EmptyBlock = '░';  // ░ pending

    private static readonly Style CompleteStyle = new(Color.Green);
    private static readonly Style ActiveStyle = new(Color.Yellow);
    private static readonly Style PendingStyle = new(Color.Grey35);

    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        var cells = CellCount(maxWidth);
        return new Measurement(cells, cells);
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        int cells = CellCount(maxWidth);
        if (totalChunks <= 0)
        {
            // Nothing to depict yet — render an all-complete/empty strip so the panel keeps its shape.
            var style = completedChunks > 0 || totalChunks == 0 ? CompleteStyle : PendingStyle;
            for (int i = 0; i < cells; i++)
                yield return new Segment(FullBlock.ToString(), style);
            yield break;
        }

        double fraction = Math.Clamp((double)completedChunks / totalChunks, 0, 1);
        double filledExact = fraction * cells;
        int fullCells = (int)Math.Floor(filledExact);
        bool hasActive = fullCells < cells && (filledExact - fullCells) > 0.0001;

        for (int i = 0; i < cells; i++)
        {
            if (i < fullCells)
                yield return new Segment(FullBlock.ToString(), CompleteStyle);
            else if (i == fullCells && hasActive)
                yield return new Segment(HalfBlock.ToString(), ActiveStyle);
            else
                yield return new Segment(EmptyBlock.ToString(), PendingStyle);
        }
    }

    private int CellCount(int maxWidth)
    {
        int width = Math.Max(1, Math.Min(maxWidth, maxCells));
        // Never show more cells than chunks (a 3-chunk transfer shouldn't pretend to be 200 cells).
        if (totalChunks > 0)
            width = Math.Min(width, Math.Max(1, totalChunks));
        return width;
    }
}
