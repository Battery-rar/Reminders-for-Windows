namespace Reminders.Windows.Services;

/// <summary>
/// Builds the reminder order that is persisted after a drop or a move command. Keeping the whole
/// decision here makes the drop, the context-menu moves and the order store agree on one rule.
/// </summary>
internal static class ReminderOrderPlan
{
    /// <summary>
    /// Returns the whole visible order with the moving row placed relative to the anchor, or null
    /// when the two rows are not siblings or the move would not change anything.
    /// </summary>
    internal static List<string>? Place<T>(IReadOnlyList<T> view, T moving, T anchor, bool after)
        where T : class, IHierarchicalReminder
    {
        var siblings = ReminderHierarchy.Siblings(view, moving).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var order = SidebarPresentation.Reorder(
            view.Where(row => siblings.Contains(row.Id)).Select(row => row.Id), moving.Id, anchor.Id, after);
        if (order is null) return null;
        var ids = new List<string>(view.Count);
        var index = 0;
        foreach (var row in view) ids.Add(siblings.Contains(row.Id) ? order[index++] : row.Id);
        return ids;
    }

    /// <summary>
    /// Applies a new visible order to the stored order. Rows outside the view keep their slot, so a
    /// completed or filtered reminder does not jump to the end when it comes back.
    /// </summary>
    internal static List<string> Merge(IReadOnlyList<string> previous, IReadOnlyList<string> requested)
    {
        var selected = requested.ToHashSet(StringComparer.Ordinal);
        var ranked = previous.ToHashSet(StringComparer.Ordinal);
        var ordered = previous.Concat(requested.Where(id => !ranked.Contains(id))).ToList();
        var next = 0;
        for (var index = 0; index < ordered.Count; index++)
            if (selected.Contains(ordered[index])) ordered[index] = requested[next++];
        return ordered;
    }
}
