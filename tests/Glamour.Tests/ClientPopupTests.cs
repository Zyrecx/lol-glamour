using Glamour.Host.Panel;

namespace Glamour.Tests;

public class ClientPopupTests
{
    [Fact]
    public void Spots_sit_on_the_measured_frame_and_scale_with_the_window()
    {
        var at720 = ClientPopup.Spots(new PixelRect(0, 0, 1280, 720))!;
        Assert.Equal(5, at720.Count);
        Assert.Equal(new PixelRect(108, 397, 115, 403), at720[0]); // the left side, x 111-112
        Assert.Equal(new PixelRect(637, 78, 643, 85), at720[3]); // the top line, rows 81-82

        var at1080 = ClientPopup.Spots(new PixelRect(100, 50, 100 + 1920, 50 + 1080))!;
        Assert.Equal(new PixelRect(100 + 162, 50 + 595, 100 + 172, 50 + 605), at1080[0]);
    }

    [Fact]
    public void No_spots_on_a_window_that_is_not_16_by_9() => Assert.Null(ClientPopup.Spots(new PixelRect(0, 0, 1280, 1024)));

    [Theory]
    [InlineData(0x46, 0x37, 0x14, true)] // the frame line
    [InlineData(0x38, 0x2C, 0x12, true)] // the line mostly over the popup's dark inside, at a smaller window
    [InlineData(0x01, 0x0A, 0x13, false)] // the popup's inside
    [InlineData(0x1B, 0x11, 0x19, false)] // the splash beside the left side
    [InlineData(0x0A, 0x15, 0x1B, false)] // champ select with no popup
    [InlineData(0xB8, 0x71, 0x55, false)] // the editor's art
    [InlineData(0xDB, 0xC0, 0x89, false)] // the close button's ring
    public void Gold_is_the_frame_colour_only(int r, int g, int b, bool gold) => Assert.Equal(gold, ClientPopup.IsGold((byte)r, (byte)g, (byte)b));
}
