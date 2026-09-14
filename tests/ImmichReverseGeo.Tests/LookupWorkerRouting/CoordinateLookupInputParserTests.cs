using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests.LookupWorkerRouting;

[TestClass]
[TestCategory("Change49")]
public sealed class CoordinateLookupInputParserTests
{
    [TestMethod]
    [DataRow("47.4647, 8.5492", 47.4647, 8.5492)]
    [DataRow("47.4647;8.5492", 47.4647, 8.5492)]
    [DataRow("47.4647|8.5492", 47.4647, 8.5492)]
    [DataRow("47.4647 8.5492", 47.4647, 8.5492)]
    [DataRow("N47.4647 E8.5492", 47.4647, 8.5492)]
    [DataRow("47.4647N 8.5492E", 47.4647, 8.5492)]
    [DataRow("S47.4647 W8.5492", -47.4647, -8.5492)]
    [DataRow("lat=-47.4647 lon=-8.5492", -47.4647, -8.5492)]
    [DataRow("47° 27' 52.92\" N 8° 32' 57.12\" E", 47.4647, 8.5492)]
    public void TryParse_PreservesSupportedPasteFormats(
        string input,
        double expectedLatitude,
        double expectedLongitude)
    {
        Assert.IsTrue(CoordinateLookupInputParser.TryParse(
            input,
            out double latitude,
            out double longitude));
        Assert.AreEqual(expectedLatitude, latitude, 0.000001);
        Assert.AreEqual(expectedLongitude, longitude, 0.000001);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not coordinates")]
    [DataRow("47 only")]
    public void TryParse_RejectsMalformedText(string input)
    {
        Assert.IsFalse(CoordinateLookupInputParser.TryParse(input, out _, out _));
    }
}
