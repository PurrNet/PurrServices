using NUnit.Framework;
using PurrNet.Services;

public class DataRegionTests
{
    [TearDown]
    public void TearDown() => DataRegion.overrideRestricted = null;

    [TestCase("Europe/Berlin", true)]
    [TestCase("Europe/London", true)]
    [TestCase("Atlantic/Canary", true)]
    [TestCase("Asia/Nicosia", true)]
    [TestCase("Romance Standard Time", true)]
    [TestCase("GMT Standard Time", true)]
    [TestCase("W. Europe Standard Time", true)]
    [TestCase("America/New_York", false)]
    [TestCase("Eastern Standard Time", false)]
    [TestCase("Asia/Tokyo", false)]
    [TestCase("UTC", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void TimeZones(string zone, bool restricted) =>
        Assert.That(DataRegion.IsRestrictedTimeZone(zone), Is.EqualTo(restricted), zone);

    [TestCase("DE", true)]
    [TestCase("fr", true)]
    [TestCase("GB", true)]
    [TestCase("NO", true)]
    [TestCase("CH", true)]
    [TestCase("RE", true)]
    [TestCase("US", false)]
    [TestCase("BR", false)]
    [TestCase("TR", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void Countries(string code, bool restricted) =>
        Assert.That(DataRegion.IsRestrictedCountry(code), Is.EqualTo(restricted), code);

    [Test]
    public void TheOverrideDecides()
    {
        DataRegion.overrideRestricted = true;
        Assert.That(DataRegion.isRestricted, Is.True);
        DataRegion.overrideRestricted = false;
        Assert.That(DataRegion.isRestricted, Is.False);
    }
}
