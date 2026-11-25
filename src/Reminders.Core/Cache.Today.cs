using System.Text.Json.Nodes;

namespace Reminders.Core;

internal sealed partial class Cache
{
    private const string TodayUndoColumn = """
        (r.completed=0 AND r.deleted=0 AND EXISTS(
            SELECT 1 FROM today_move_history m
            WHERE m.reminder_id=r.id AND m.applied_due_date=r.due_date AND m.applied_all_day=r.all_day
        )) AS can_undo_today
        """;

    internal JsonObject? BeforeTodayMove(string id) =>
        Query("SELECT original_due_date AS due_date, original_all_day AS all_day FROM today_move_history WHERE reminder_id=@p0", id)
            .FirstOrDefault() as JsonObject;

    internal void RememberTodayMove(string id, JsonObject previous, JsonObject applied) => Execute("""
        INSERT INTO today_move_history(reminder_id,original_due_date,original_all_day,applied_due_date,applied_all_day)
        VALUES(@p0,@p1,@p2,@p3,@p4)
        ON CONFLICT(reminder_id) DO UPDATE SET original_due_date=excluded.original_due_date,original_all_day=excluded.original_all_day,
            applied_due_date=excluded.applied_due_date,applied_all_day=excluded.applied_all_day
        """, id, previous.Text("due_date"), previous.Flag("all_day"), applied.Text("due_date"), applied.Flag("all_day"));
}
