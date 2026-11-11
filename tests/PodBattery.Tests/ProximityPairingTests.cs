namespace PodBattery.Tests;

// Sample data is real AirPods Pro 2 (USB-C) broadcasts captured with this app's scanner.
public class ProximityPairingTests
{
    private static PodAdvert? Parse(string hex) => ProximityPairing.TryParse(Convert.FromHexString(hex));

    [Fact]
    public void Decodes_earbuds_in_normal_order()
    {
        var ad = Parse("07190124202B768F110005")!;

        Assert.Equal(0x2024, ad.Model);
        Assert.Equal(60, ad.Left);
        Assert.Equal(70, ad.Right);
        Assert.Null(ad.Case);
        Assert.False(ad.LeftCharging || ad.RightCharging || ad.CaseCharging);
        Assert.Equal(0x11, ad.Lid);
        Assert.False(ad.CaseOpen);   // worn
    }

    [Theory]
    [InlineData("071901242034FAFA31000029", true)]    // owner's case opened next to the PC (both in case, lid open)
    [InlineData("071901242025AADA310000", true)]
    [InlineData("071901242035AA8F390000", false)]     // lid just closed (0x08 set)
    [InlineData("071901242022F68F100005", false)]     // one earbud in someone's ear
    public void Recognises_an_open_case(string hex, bool open) => Assert.Equal(open, Parse(hex)!.CaseOpen);

    [Fact]
    public void Decodes_swapped_order_and_charging_flags()
    {
        var ad = Parse("071901242014AAB2310004")!;

        Assert.Equal(100, ad.Left);
        Assert.Equal(100, ad.Right);
        Assert.Equal(20, ad.Case);
        Assert.True(ad.LeftCharging);
        Assert.True(ad.RightCharging);
        Assert.False(ad.CaseCharging);
        Assert.Equal(0x31, ad.Lid);
    }

    [Fact]
    public void Unknown_levels_are_null_and_never_charging()
    {
        var ad = Parse("071901242022F68F100005")!;

        Assert.Equal(60, ad.Left);
        Assert.Null(ad.Right);
        Assert.False(ad.RightCharging);
    }

    [Theory]
    [InlineData("0F05C01A2B3C4D")]          // a different Apple message type
    [InlineData("07190024202B768F110005")]  // pairing mode: different layout
    [InlineData("0719012420")]              // truncated
    [InlineData("")]
    public void Rejects_other_messages(string hex) => Assert.Null(Parse(hex));

    [Fact]
    public void Names_known_and_unknown_models()
    {
        Assert.Equal("AirPods Pro 2 (USB-C)", ProximityPairing.ModelName(0x2024));
        Assert.Equal("AirPods (model 1234)", ProximityPairing.ModelName(0x1234));
    }
}
