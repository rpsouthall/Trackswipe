using Anchor.Models;
using Xunit;

namespace Anchor.Tests.Models;

public class HotkeyGestureTests
{
    [Fact]
    public void Default_is_Ctrl_Alt_A()
    {
        Assert.Equal("Ctrl+Alt+A", HotkeyGesture.Default.ToString());
        Assert.True(HotkeyGesture.Default.IsValid);
    }

    [Theory]
    [InlineData("Ctrl+Alt+A")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("Win+Space")]
    [InlineData("Alt+PageDown")]
    [InlineData("Ctrl+Alt+Shift+Win+7")]
    public void Round_trips_through_its_text_form(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(text, gesture.ToString());
    }

    [Theory]
    // Casing, spacing and the long modifier spellings all have to survive a hand-edited config.
    [InlineData("ctrl+alt+a")]
    [InlineData("Control + Alt + A")]
    [InlineData("ALT+CTRL+A")]
    public void Parsing_is_tolerant_of_spelling_and_order(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(HotkeyGesture.Default, gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]          // no key
    [InlineData("Ctrl+Alt")]       // modifiers only
    [InlineData("Ctrl+Alt+A+B")]   // two non-modifier keys
    [InlineData("Ctrl+Alt+F25")]   // past the end of the function keys
    [InlineData("Ctrl+Alt+Pause")] // not in the assignable table
    public void Rejects_input_it_cannot_round_trip(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
    }

    [Fact]
    public void A_bare_key_parses_but_is_not_valid_to_register()
    {
        // Parsing and registrability are separate: "A" is a well-formed gesture, but Windows
        // would hand Anchor every press of the A key, so it must not be offered.
        Assert.True(HotkeyGesture.TryParse("A", out var gesture));
        Assert.False(gesture.IsValid);
    }

    [Fact]
    public void Shift_alone_is_not_enough_of_a_modifier()
    {
        Assert.True(HotkeyGesture.TryParse("Shift+A", out var gesture));
        Assert.False(gesture.IsValid);
    }

    [Theory]
    [InlineData("Ctrl+A")]
    [InlineData("Alt+A")]
    [InlineData("Win+A")]
    public void One_real_modifier_is_enough(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.True(gesture.IsValid);
    }

    [Fact]
    public void Modifiers_map_onto_the_Win32_MOD_constants()
    {
        // RegisterHotKey is handed (uint)Modifiers directly, so these values are load-bearing.
        Assert.Equal(0x0001, (int)HotkeyModifiers.Alt);
        Assert.Equal(0x0002, (int)HotkeyModifiers.Control);
        Assert.Equal(0x0004, (int)HotkeyModifiers.Shift);
        Assert.Equal(0x0008, (int)HotkeyModifiers.Windows);
    }

    [Theory]
    [InlineData("A", 0x41)]
    [InlineData("Z", 0x5A)]
    [InlineData("0", 0x30)]
    [InlineData("F1", 0x70)]
    [InlineData("F24", 0x87)]
    [InlineData("Space", 0x20)]
    [InlineData("Delete", 0x2E)]
    public void Key_names_map_to_the_right_virtual_key_codes(string name, uint code)
    {
        Assert.Equal(code, HotkeyGesture.Code(name));
        Assert.Equal(name, HotkeyGesture.Name(code));
    }

    [Fact]
    public void Unassignable_key_codes_have_no_name()
    {
        Assert.Null(HotkeyGesture.Name(0x13)); // VK_PAUSE
        Assert.Null(HotkeyGesture.Code("Pause"));
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var a = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x41);
        var b = new HotkeyGesture(HotkeyModifiers.Alt | HotkeyModifiers.Control, 0x41);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new HotkeyGesture(HotkeyModifiers.Control, 0x41));
    }
}
