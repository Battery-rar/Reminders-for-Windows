using Reminders.Windows.Services;

internal static class ReminderOrderPlanChecks
{
    private sealed record Row(string Id, string? ParentId = null, string ListId = "L") : IHierarchicalReminder
    {
        public bool ContextOnly => false;
        public string? DueDate => null;
        public bool AllDay => false;
        public bool Completed => false;
        public bool Deleted => false;
        public string Title => Id;
        public long Priority => 0;
    }

    internal static void Run(Action<bool, string> check)
    {
        static bool Is(IReadOnlyList<string>? ids, params string[] expected) => ids is not null && ids.SequenceEqual(expected);

        var roots = new[] { new Row("a"), new Row("b"), new Row("c") };
        check(Is(ReminderOrderPlan.Place(roots, roots[2], roots[0], after: true), "a", "c", "b"),
            "dropping below a row inserts right after it");
        check(Is(ReminderOrderPlan.Place(roots, roots[0], roots[2], after: false), "b", "a", "c"),
            "dropping above a row inserts right before it");
        check(ReminderOrderPlan.Place(roots, roots[1], roots[0], after: true) is null,
            "a drop that would not move the row is rejected");

        var family = new[] { new Row("p"), new Row("p1", "p"), new Row("p2", "p"), new Row("q"), new Row("q1", "q") };
        check(Is(ReminderOrderPlan.Place(family, family[0], family[3], after: true), "q", "p1", "p2", "p", "q1"),
            "moving a parent leaves its subtasks in their slots, which keeps them attached to the parent");
        check(Is(ReminderOrderPlan.Place(family, family[2], family[1], after: false), "p", "p2", "p1", "q", "q1"),
            "a subtask moves among its own siblings only");
        check(ReminderOrderPlan.Place(family, family[1], family[4], after: true) is null,
            "a drop onto a row at another level is rejected");

        var orphan = new[] { new Row("orphan", "gone"), new Row("a") };
        check(Is(ReminderOrderPlan.Place(orphan, orphan[0], orphan[1], after: true), "a", "orphan"),
            "a row whose parent is not in the view is ordered as a top-level row");

        check(Is(ReminderOrderPlan.Merge(["a", "x", "b", "c"], ["c", "a", "b"]), "c", "x", "a", "b"),
            "a hidden reminder keeps its stored slot when the visible order changes");
        check(Is(ReminderOrderPlan.Merge(["a", "b"], ["b", "new", "a"]), "b", "new", "a"),
            "a reminder ranked for the first time lands where the view put it");
        check(Is(ReminderOrderPlan.Merge(["a", "b", "c"], ["a", "b", "c"]), "a", "b", "c"),
            "applying the same order twice leaves it unchanged");
        check(Is(ReminderOrderPlan.Merge([], ["b", "a"]), "b", "a"),
            "the first reorder in a view starts from the order the view sent");
    }
}
