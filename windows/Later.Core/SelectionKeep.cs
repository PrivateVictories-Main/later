namespace Later.Core;

/// What a change to the list does to the selection: what's gone is deselected, and when the selection was among
/// them and nothing is left, the page after the last of them (or else before it) is selected, so working down a list
/// takes one click per page. When some of the selection stays, the keyboard moves on from the nearest of it.
public static class SelectionKeep
{
    /// `rows` are the rows after the change, `changed` the pages it touched, `before` the rows before it.
    public static (HashSet<Guid> Selected, Guid? Cursor) Keep(HashSet<Guid> selected, Guid? cursor, IReadOnlyList<Guid> rows,
                                                              ISet<Guid> changed, IReadOnlyList<Guid> before)
    {
        var present = rows.ToHashSet();
        bool wasSelected = changed.Overlaps(selected);
        var kept = selected.Where(present.Contains).ToHashSet();
        if (wasSelected && kept.Count == 0)
        {
            int last = -1;
            for (int i = before.Count - 1; i >= 0; i--) if (changed.Contains(before[i])) { last = i; break; }
            if (last >= 0)
            {
                Guid? next = null;
                for (int i = last + 1; i < before.Count; i++) if (present.Contains(before[i])) { next = before[i]; break; }
                if (next is null)
                    for (int i = last - 1; i >= 0; i--) if (present.Contains(before[i])) { next = before[i]; break; }
                // select(id): the page alone is the selection, and the keyboard is on it.
                var only = new HashSet<Guid>();
                if (next is { } id) only.Add(id);
                return (only, next);
            }
        }
        if (cursor is { } gone && !present.Contains(gone)) cursor = NearestSelected(kept, gone, before);
        return (kept, cursor);
    }

    /// The selected row closest to `id` in `rows` (the earlier one on a tie); null when nothing is selected.
    static Guid? NearestSelected(HashSet<Guid> selected, Guid id, IReadOnlyList<Guid> rows)
    {
        int at = -1;
        for (int i = 0; i < rows.Count; i++) if (rows[i] == id) { at = i; break; }
        if (at < 0) return null;
        int best = -1;
        for (int i = 0; i < rows.Count; i++)
            if (selected.Contains(rows[i]) && (best < 0 || Math.Abs(i - at) < Math.Abs(best - at))) best = i;
        return best < 0 ? null : rows[best];
    }
}
