using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ScheduleEditorStateTests
{
    [TestMethod]
    public void FromCron_DailyCron_ParsesDailyMode()
    {
        var state = ScheduleEditorState.FromCron("0 2 * * *");

        Assert.AreEqual(ScheduleEditorState.ModeDaily, state.Mode);
        Assert.AreEqual("02:00", state.Time);
        Assert.AreEqual("0 2 * * *", state.ToCron());
    }

    [TestMethod]
    public void FromCron_EveryMinutesCron_ParsesIntervalMode()
    {
        var state = ScheduleEditorState.FromCron("*/15 * * * *");

        Assert.AreEqual(ScheduleEditorState.ModeEveryMinutes, state.Mode);
        Assert.AreEqual(15, state.MinuteInterval);
        Assert.AreEqual("*/15 * * * *", state.ToCron());
    }

    [TestMethod]
    [DataRow("60 * * * *")]
    [DataRow("99 * * * *")]
    [DataRow("*/0 * * * *")]
    [DataRow("*/60 * * * *")]
    [DataRow("0 */0 * * *")]
    [DataRow("0 */24 * * *")]
    [DataRow("60 */6 * * *")]
    [DataRow("60 2 * * *")]
    [DataRow("0 24 * * *")]
    [DataRow("60 2 * * MON")]
    [DataRow("0 24 * * MON")]
    [DataRow("٠ * * * *")]
    [DataRow("*/١ * * * *")]
    [DataRow("٠ */١ * * *")]
    [DataRow("٠ ٢ * * *")]
    [DataRow("٠ ٢ * * MON")]
    public void FromCron_InvalidPresetFields_RemainCustom(string cron)
    {
        var state = ScheduleEditorState.FromCron(cron);

        Assert.AreEqual(ScheduleEditorState.ModeCustom, state.Mode);
    }

    [TestMethod]
    [DataRow("0 * * * *", ScheduleEditorState.ModeHourly)]
    [DataRow("59 * * * *", ScheduleEditorState.ModeHourly)]
    [DataRow("*/1 * * * *", ScheduleEditorState.ModeEveryMinutes)]
    [DataRow("*/59 * * * *", ScheduleEditorState.ModeEveryMinutes)]
    [DataRow("0 */1 * * *", ScheduleEditorState.ModeEveryHours)]
    [DataRow("59 */23 * * *", ScheduleEditorState.ModeEveryHours)]
    [DataRow("0 0 * * *", ScheduleEditorState.ModeDaily)]
    [DataRow("59 23 * * *", ScheduleEditorState.ModeDaily)]
    [DataRow("0 0 * * SUN", ScheduleEditorState.ModeWeekly)]
    [DataRow("59 23 * * MON", ScheduleEditorState.ModeWeekly)]
    public void FromCron_ValidPresetBoundaries_RoundTrip(string cron, string expectedMode)
    {
        var state = ScheduleEditorState.FromCron(cron);

        Assert.AreEqual(expectedMode, state.Mode);
        Assert.AreEqual(cron, state.ToCron());
    }

    [TestMethod]
    public void FromCron_LowerCaseWeeklyDay_PreservesSchedule()
    {
        var state = ScheduleEditorState.FromCron("30 6 * * fri");

        Assert.AreEqual(ScheduleEditorState.ModeWeekly, state.Mode);
        Assert.AreEqual("30 6 * * FRI", state.ToCron());
    }

    [TestMethod]
    public void ToCron_WeeklyMode_BuildsWeeklyCron()
    {
        var state = new ScheduleEditorState
        {
            Mode = ScheduleEditorState.ModeWeekly,
            Time = "06:30",
            WeeklyDay = "FRI"
        };

        Assert.AreEqual("30 6 * * FRI", state.ToCron());
        Assert.AreEqual("Every Friday at 06:30", state.Describe());
    }
}
