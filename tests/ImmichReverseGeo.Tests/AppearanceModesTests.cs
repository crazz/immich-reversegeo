using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class AppearanceModesTests
{
    [TestMethod]
    [DataRow(null, AppearanceModes.Auto)]
    [DataRow("", AppearanceModes.Auto)]
    [DataRow("   ", AppearanceModes.Auto)]
    [DataRow("bogus", AppearanceModes.Auto)]
    [DataRow("auto", AppearanceModes.Auto)]
    [DataRow("AUTO", AppearanceModes.Auto)]
    [DataRow("light", AppearanceModes.Light)]
    [DataRow("LIGHT", AppearanceModes.Light)]
    [DataRow("dark", AppearanceModes.Dark)]
    [DataRow("Dark", AppearanceModes.Dark)]
    public void NormalizeMode_MapsKnownAndUnknownValues(string? input, string expected)
    {
        Assert.AreEqual(expected, AppearanceModes.NormalizeMode(input));
    }
}
