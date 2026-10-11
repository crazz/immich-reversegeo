using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class SettingsSaveStateTests
{
    [TestMethod]
    public void CompletingSave_LeavesLaterEditsPending()
    {
        var config = new AppConfig();
        var state = new SettingsSaveState();
        state.Load(config);
        config.Processing.BatchSize = 75;
        var request = state.BeginSave(config)!;

        config.Processing.BatchSize = 90;
        state.CompleteSave();

        Assert.AreEqual(75, request.Processing.BatchSize);
        Assert.AreEqual(90, config.Processing.BatchSize);
        Assert.IsTrue(state.HasChanges(config));
        Assert.IsFalse(state.IsSaving);
        var nextRequest = state.BeginSave(config)!;
        Assert.AreEqual(90, nextRequest.Processing.BatchSize);
        state.CompleteSave();
        Assert.IsFalse(state.HasChanges(config));
    }

    [TestMethod]
    public void FailedSave_KeepsEditsAndAllowsRetry()
    {
        var config = new AppConfig();
        var state = new SettingsSaveState();
        state.Load(config);
        config.Schedule.Enabled = false;
        state.BeginSave(config);
        state.FailSave("Config volume is read-only");

        Assert.IsTrue(state.HasChanges(config));
        Assert.IsFalse(state.IsSaving);
        Assert.AreEqual("Config volume is read-only", state.Error);
        Assert.IsNotNull(state.BeginSave(config));
        Assert.IsNull(state.Error);
    }

    [TestMethod]
    public void SaveSnapshot_IsIndependentAndRepeatSubmissionIsIgnored()
    {
        var config = new AppConfig();
        var state = new SettingsSaveState();
        state.Load(config);
        config.Processing.CityResolver.CountryOverrides["CHE"] = new CityResolverProfile();
        var request = state.BeginSave(config)!;
        config.Processing.CityResolver.CountryOverrides["CHE"].PreferredSubtypes.Clear();

        Assert.IsTrue(request.Processing.CityResolver.CountryOverrides["CHE"].PreferredSubtypes.Count > 0);
        Assert.IsNull(state.BeginSave(config));
    }
}
