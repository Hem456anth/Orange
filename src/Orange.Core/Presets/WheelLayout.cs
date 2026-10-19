namespace Orange.Core.Presets;

/// <summary>
/// The user's petal arrangement per file kind and wheel: hidden operation ids and a preferred order.
/// Ids not mentioned keep their default position after the ordered ones; hidden ids never show.
/// </summary>
public sealed class WheelLayout
{
    /// <summary>Key: "Image/Convert", "Video/Tool"…</summary>
    public Dictionary<string, List<string>> Order { get; set; } = new();
    public Dictionary<string, List<string>> Hidden { get; set; } = new();

    public static string Key(FileKind kind, OperationGroup group) => $"{kind}/{group}";

    public IReadOnlyList<OperationDescriptor> Apply(FileKind kind, OperationGroup group, IReadOnlyList<OperationDescriptor> ops)
    {
        var key = Key(kind, group);
        var hidden = Hidden.TryGetValue(key, out var h) ? h.ToHashSet() : new HashSet<string>();
        var order = Order.TryGetValue(key, out var o) ? o : new List<string>();
        return ops.Where(x => !hidden.Contains(x.Id))
            .Select((x, i) => (x, rank: order.IndexOf(x.Id) is var r && r >= 0 ? r : order.Count + i))
            .OrderBy(t => t.rank).Select(t => t.x).ToList();
    }

    public bool IsHidden(FileKind kind, OperationGroup group, string id) =>
        Hidden.TryGetValue(Key(kind, group), out var h) && h.Contains(id);

    public void SetHidden(FileKind kind, OperationGroup group, string id, bool hidden)
    {
        var key = Key(kind, group);
        if (!Hidden.TryGetValue(key, out var h)) Hidden[key] = h = new();
        h.Remove(id);
        if (hidden) h.Add(id);
    }

    /// <summary>Moves an id one place up (-1) or down (+1) within the current visible order.</summary>
    public void Move(FileKind kind, OperationGroup group, IReadOnlyList<OperationDescriptor> all, string id, int delta)
    {
        var current = Apply(kind, group, all).Select(x => x.Id).ToList();
        int i = current.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= current.Count) return;
        (current[i], current[j]) = (current[j], current[i]);
        Order[Key(kind, group)] = current;
    }
}
