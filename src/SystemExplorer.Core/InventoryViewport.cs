namespace SystemExplorer.Core;

public enum InventoryEdge { Anchor, Top, Bottom }

public static class InventoryViewport
{
    public static InventoryEdge FollowLatest(bool live, bool timeOrdered, bool descending, double offset, double extent)
    {
        if (!live || !timeOrdered || !double.IsFinite(offset) || !double.IsFinite(extent) || offset < 0 || extent < 0)
            return InventoryEdge.Anchor;
        return descending
            ? offset <= 1 ? InventoryEdge.Top : InventoryEdge.Anchor
            : offset >= extent - 1 ? InventoryEdge.Bottom : InventoryEdge.Anchor;
    }
}
