using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit;

namespace Anchor.UITests;

/// <summary>
/// The launch-and-look-at-it tests: does Anchor come up, does the dock render its seeded items,
/// and do the windows it can open actually open. Everything here goes through UI Automation, so
/// it exercises the same tree Narrator sees — a regression in the accessibility surface fails
/// these tests too, which is the point.
/// </summary>
public sealed class DockSmokeTests
{
    /// <summary>The dock Anchor seeds on a genuine first run (see <c>DockWindow.SeedDefaults</c>).</summary>
    private static readonly string[] SeededItems =
    {
        "File Explorer", "Notepad", "Calculator", "Home", "WinUI Gallery",
    };

    [UIFact]
    public void The_dock_window_appears_on_launch()
    {
        using var app = AnchorApp.Launch();

        var dock = app.WaitForDock();

        Assert.Equal(AnchorApp.DockWindowTitle, dock.Name);
        // Borderless and off the taskbar, but still a real, non-empty window on screen.
        Assert.True(dock.BoundingRectangle.Width > 0, "The dock has no width.");
        Assert.True(dock.BoundingRectangle.Height > 0, "The dock has no height.");
    }

    [UIFact]
    public void A_first_run_dock_shows_its_seeded_items_and_the_gear()
    {
        // Every launch here gets an empty ANCHOR_DATA_DIR, so this is always a true first run:
        // the dock seeds its defaults and must render them rather than the empty-state pill.
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        var buttons = AnchorApp.FindButtons(dock);
        var names = buttons.Select(b => b.Name).ToList();

        // Each item is a real, named, invokable Button — which is what makes the dock keyboard-
        // and screen-reader-usable in the first place.
        foreach (var seeded in SeededItems)
            Assert.Contains(seeded, names);

        Assert.NotNull(AnchorApp.FindById(dock, "AnchorSettingsButton"));
    }

    [UIFact]
    public void The_gear_opens_the_Settings_window_and_its_pages_switch()
    {
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);

        var settings = app.WaitForWindow("Anchor Settings");
        Assert.NotNull(AnchorApp.FindById(settings, "SettingsNavGeneral"));

        // The Apps page is collapsed until its nav item is selected; selecting it has to reveal
        // the page, which is the wiring most likely to rot as the window grows.
        AnchorApp.Press(AnchorApp.FindById(settings, "SettingsNavApps")!);
        Assert.NotNull(AnchorApp.FindButtonByName(settings, "Add new"));

        settings.Close();
        Assert.True(app.WaitForWindowToClose("Anchor Settings"), "The Settings window stayed open.");
    }

    [UIFact]
    public void The_Docks_page_lists_the_one_dock_a_first_run_has()
    {
        // Multi-dock support turned the old per-dock General settings into a page of their own.
        // A fresh profile has exactly one dock, and it has to show up there.
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);
        var settings = app.WaitForWindow("Anchor Settings");

        AnchorApp.Press(AnchorApp.FindById(settings, "SettingsNavDocks")!);
        Assert.NotNull(AnchorApp.FindButtonByName(settings, "Add dock"));

        // Each dock's card carries an editable name, so an edit box is the marker that a card was
        // built at all rather than the list rendering empty.
        var list = AnchorApp.Retry(() =>
        {
            var boxes = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            return boxes.Length > 0 ? boxes : null;
        });
        Assert.NotNull(list);

        settings.Close();
    }

    [UIFact]
    public void The_Appearance_page_offers_the_personalization_controls()
    {
        // Density, accent tint and magnification are app-wide and live on their own page. Finding the
        // page's own automation id proves the nav entry, the tag routing and the panel all line
        // up — the wiring that silently rots whenever a page is added.
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);
        var settings = app.WaitForWindow("Anchor Settings");

        AnchorApp.Press(AnchorApp.FindById(settings, "SettingsNavAppearance")!);
        Assert.NotNull(AnchorApp.FindById(settings, "SettingsAppearancePanel"));

        // A collapsed page is absent from the automation tree entirely, so a combo box being
        // reachable here is proof the Appearance page is the one showing.
        var combos = AnchorApp.Retry(() =>
        {
            var found = settings.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox));
            return found.Length > 0 ? found : null;
        });
        Assert.NotNull(combos);

        settings.Close();
    }

    [UIFact]
    public void The_Shortcuts_page_shows_the_summon_and_search_shortcuts()
    {
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);
        var settings = app.WaitForWindow("Anchor Settings");

        AnchorApp.Press(AnchorApp.FindById(settings, "SettingsNavShortcuts")!);
        Assert.NotNull(AnchorApp.FindById(settings, "SettingsShortcutsPanel"));

        // Both capture buttons are built in code-behind rather than declared in XAML, so this is
        // the check that they were actually added to their cards — and that each announces which
        // shortcut it is for, which is the only thing distinguishing them to a screen reader.
        // The summon shortcut is seeded with Ctrl+Alt+A; the search one starts unassigned.
        Assert.NotNull(AnchorApp.FindButtonByName(settings, "Summon the docks: Ctrl+Alt+A"));
        Assert.NotNull(AnchorApp.FindButtonByName(settings, "Quick-launch search: None"));

        settings.Close();
    }

    [UIFact]
    public void A_group_opens_a_fly_out_bar_of_its_children()
    {
        // The fly-out bar renders its cells imperatively rather than from the strip's template,
        // so nothing else in this suite would catch it failing to build them. A child is only
        // reachable through the bar — it never appears on the strip itself — which makes finding
        // one proof that the bar opened and populated.
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        var group = CreateGroupWithFileExplorer(app, dock);
        AnchorApp.Press(group);

        var child = AnchorApp.Retry(() => AnchorApp.FindButtonByName(dock, "File Explorer"));
        Assert.NotNull(child);
    }

    [UIFact]
    public void A_group_opens_a_fly_out_bar_on_hover_too()
    {
        // Same fly-out as above, but landed on rather than clicked: the strip's own pointer
        // tracking (DockWindow.TrackStripPointer) opens a group's bar as soon as the cursor is
        // over its icon, so the bar comes up without an Invoke/Click at all.
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        var group = CreateGroupWithFileExplorer(app, dock);
        FlaUI.Core.Input.Mouse.MoveTo(group.GetClickablePoint());

        var child = AnchorApp.Retry(() => AnchorApp.FindButtonByName(dock, "File Explorer"));
        Assert.NotNull(child);
    }

    // ---- Group fly-out: hover, and click ----------------------------------
    //
    // These drive the physical cursor rather than the invoke pattern, because the behavior under
    // test IS pointer behavior: the bar is a light-dismiss fly-out, and how it reacts to the
    // cursor arriving on the icon, crossing to the bar, leaving both, or pressing while it is
    // already up is exactly what has been getting this wrong. An Invoke would skip all of it.
    //
    // The dock is seeded with the group already in it (see GroupDockJson) instead of built
    // through Settings and the new-group window: that route is a long way to travel before the
    // behavior under test is even reachable, and every step of it is a chance to fail for an
    // unrelated reason.

    /// <summary>A floating dock holding one group, "Group", with "File Explorer" inside it.
    /// Targets are written with forward slashes: this is JSON, where a lone backslash is an
    /// escape, and a mis-escaped path costs the whole file — Anchor falls back to a default,
    /// seeded config on a parse error, so the group under test would simply not be there.</summary>
    private static string GroupDockJson(bool openOnHover) => $$"""
    {
      "Language": "en",
      "Seeded": true,
      "GroupOpenOnHover": {{(openOnHover ? "true" : "false")}},
      "Docks": [
        {
          "Name": "Dock 1",
          "Snapped": false,
          "AutoHide": false,
          "FreeX": 400,
          "FreeY": 240,
          "Items": [
            {
              "Kind": "Group",
              "DisplayName": "Group",
              "Children": [
                {
                  "Kind": "Application",
                  "DisplayName": "File Explorer",
                  "Target": "C:/Windows/explorer.exe"
                }
              ]
            }
          ]
        }
      ]
    }
    """;

    /// <summary>
    /// A point well clear of both the dock and the bar it opens. Below the dock on purpose: the
    /// bar opens upward out of a floating dock, so "below" is away from both at once.
    /// </summary>
    private static System.Drawing.Point AwayFromTheDock(Window dock) => new(
        dock.BoundingRectangle.Left + dock.BoundingRectangle.Width / 2,
        dock.BoundingRectangle.Bottom + 200);

    /// <summary>
    /// Lands the cursor on a dock icon, from somewhere else. The detour matters: hover is driven
    /// by pointer <em>movement</em> over the strip, and a "move" to where the cursor already is
    /// produces no movement at all — so a test that inherits the cursor from the one before it
    /// would find nothing hovered and blame the app for it.
    /// </summary>
    private static void HoverIcon(Window dock, AutomationElement icon)
    {
        LeaveTheDock(dock);
        TestMouse.GlideTo(icon.GetClickablePoint());
        Thread.Sleep(300);
    }

    /// <summary>Takes the cursor well away from the dock and the bar, and waits out the close.</summary>
    private static void LeaveTheDock(Window dock)
    {
        TestMouse.GlideTo(AwayFromTheDock(dock));
        Thread.Sleep(300);
    }

    /// <summary>True once the group's bar is showing its one child, or once it is not.</summary>
    private static bool BarIsOpen(Window dock) =>
        dock.FindFirstDescendant(cf => cf.ByName("File Explorer").And(cf.ByControlType(ControlType.Button)))
            is not null;

    [UIFact]
    public void A_group_opens_its_fly_out_bar_on_hover()
    {
        using var app = AnchorApp.Launch(GroupDockJson(openOnHover: true));
        var dock = app.WaitForDock();
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);

        HoverIcon(dock, group!);

        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)),
            "Hovering the group icon did not open its fly-out bar.");
    }

    [UIFact]
    public void A_hovered_group_bar_stays_open_while_the_cursor_is_on_the_icon()
    {
        // The regression this exists for: the bar's own light-dismiss layer used to cut the strip
        // off from the pointer, so the strip decided the cursor had left, closed the bar, got the
        // pointer back, saw the cursor still on the icon, and opened it again — a flicker for as
        // long as the cursor sat there. Holding still for a second and finding the bar still up
        // (and still up after that) is what says that loop is gone.
        using var app = AnchorApp.Launch(GroupDockJson(openOnHover: true));
        var dock = app.WaitForDock();
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);

        HoverIcon(dock, group!);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)), "The bar never opened.");

        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(250);
            Assert.True(BarIsOpen(dock),
                "The bar closed while the cursor was still resting on the group's icon.");
        }
    }

    [UIFact]
    public void A_hovered_group_bar_closes_once_the_cursor_leaves()
    {
        using var app = AnchorApp.Launch(GroupDockJson(openOnHover: true));
        var dock = app.WaitForDock();
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);

        HoverIcon(dock, group!);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)), "The bar never opened.");

        LeaveTheDock(dock);

        Assert.True(AnchorApp.WaitUntil(() => !BarIsOpen(dock)),
            "The bar stayed open after the cursor left both it and the group's icon.");
    }

    [UIFact]
    public void A_second_click_on_a_group_hides_its_fly_out_bar()
    {
        // "First click shows it, second click hides it" — the half that keeps going wrong is the
        // second one, because the press that lands on the icon also light-dismisses the bar, so
        // the click handler can find nothing left to toggle and open it straight back up.
        using var app = AnchorApp.Launch(GroupDockJson(openOnHover: false));
        var dock = app.WaitForDock();
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);

        HoverIcon(dock, group!);
        var icon = group!.GetClickablePoint();
        FlaUI.Core.Input.Mouse.Click(icon);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)),
            "The first click did not open the group's fly-out bar.");

        FlaUI.Core.Input.Mouse.Click(icon);
        Assert.True(AnchorApp.WaitUntil(() => !BarIsOpen(dock)),
            "The second click did not hide the group's fly-out bar.");

        // And it stays hidden: nothing may re-open it while the cursor rests on the icon.
        Thread.Sleep(700);
        Assert.False(BarIsOpen(dock), "The bar re-opened by itself after the second click.");
    }

    [UIFact]
    public void A_click_closes_a_group_bar_that_hover_opened_and_it_stays_closed()
    {
        // Hover mode and click toggling have to coexist on the same icon, and this is where they
        // collide: the cursor is resting on the icon — which is what hover uses to keep the bar
        // open — at the moment the click asks for it to be closed. If the click does not also
        // hold hover off, the very next pointer move puts the bar straight back up and the click
        // reads as having done nothing.
        using var app = AnchorApp.Launch(GroupDockJson(openOnHover: true));
        var dock = app.WaitForDock();
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);

        HoverIcon(dock, group!);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)), "The bar never opened on hover.");

        var icon = group!.GetClickablePoint();
        FlaUI.Core.Input.Mouse.Click(icon);
        Assert.True(AnchorApp.WaitUntil(() => !BarIsOpen(dock)),
            "Clicking the icon did not hide the bar hover had opened.");

        // The cursor has not moved off the icon: hover must not undo the click.
        Thread.Sleep(1200);
        Assert.False(BarIsOpen(dock), "Hover re-opened the bar the click had just closed.");

        // A further click opens it again — the toggle is not stuck shut either.
        FlaUI.Core.Input.Mouse.Click(icon);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)),
            "A click did not re-open the bar.");

        // And leaving re-arms hover for next time, rather than the click's suppression sticking.
        LeaveTheDock(dock);
        Assert.True(AnchorApp.WaitUntil(() => !BarIsOpen(dock)), "The bar stayed open on leaving.");
        HoverIcon(dock, group!);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)),
            "Hover no longer opened the bar after a click had closed it earlier.");
    }

    [UIFact]
    public void With_hover_off_a_group_ignores_the_cursor_and_waits_for_a_click()
    {
        using var app = AnchorApp.Launch(GroupDockJson(openOnHover: false));
        var dock = app.WaitForDock();
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);

        HoverIcon(dock, group!);
        var icon = group!.GetClickablePoint();
        Thread.Sleep(1000);
        Assert.False(BarIsOpen(dock), "The group opened on hover with \"On click only\" set.");

        FlaUI.Core.Input.Mouse.Click(icon);
        Assert.True(AnchorApp.WaitUntil(() => BarIsOpen(dock)),
            "A click did not open the group's fly-out bar.");
    }

    /// <summary>
    /// Builds a group holding "File Explorer" through the Settings list — the one route to "move
    /// to group" that does not depend on driving a context menu over the dock strip — and returns
    /// the group's own button on the strip.
    /// </summary>
    private static AutomationElement CreateGroupWithFileExplorer(AnchorApp app, Window dock)
    {
        var notepad = AnchorApp.FindButtonByName(dock, "Notepad");
        Assert.NotNull(notepad);

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);
        var settings = app.WaitForWindow("Anchor Settings");
        AnchorApp.Press(AnchorApp.FindById(settings, "SettingsNavApps")!);

        var addToGroup = AnchorApp.FindButtonByName(settings, "Move to group");
        Assert.NotNull(addToGroup);
        AnchorApp.Press(addToGroup!);

        var newGroup = AnchorApp.Retry(() => settings.FindFirstDescendant(cf => cf.ByName("New group…")));
        Assert.NotNull(newGroup);
        AnchorApp.Press(newGroup!);

        var create = AnchorApp.FindButtonByName(dock, "Create");
        Assert.NotNull(create);
        AnchorApp.Press(create!);

        settings.Close();

        // The group is now on the strip.
        var group = AnchorApp.FindButtonByName(dock, "Group");
        Assert.NotNull(group);
        return group!;
    }

    /// <summary>A floating dock holding one group, "Group", with two children — enough to prove a
    /// collapse hides more than a single row and an expand brings all of them back.</summary>
    private static string TwoChildGroupDockJson() => """
    {
      "Language": "en",
      "Seeded": true,
      "Docks": [
        {
          "Name": "Dock 1",
          "Snapped": false,
          "AutoHide": false,
          "FreeX": 400,
          "FreeY": 240,
          "Items": [
            {
              "Kind": "Group",
              "DisplayName": "Group",
              "Children": [
                { "Kind": "Application", "DisplayName": "File Explorer", "Target": "C:/Windows/explorer.exe" },
                { "Kind": "Application", "DisplayName": "Notepad", "Target": "C:/Windows/System32/notepad.exe" }
              ]
            }
          ]
        }
      ]
    }
    """;

    /// <summary>True once an Apps &amp; links row named <paramref name="name"/> is on screen (or
    /// once it is not) — a row is a plain <c>Border</c>/<c>TextBlock</c> rather than a Button, so
    /// this looks the item's display name up by name alone, unlike a fly-out bar cell.</summary>
    private static bool RowExists(Window settings, string name) =>
        settings.FindFirstDescendant(cf => cf.ByName(name)) is not null;

    [UIFact]
    public void The_collapse_button_hides_and_the_expand_button_restores_a_groups_children_rows()
    {
        // Seeded directly rather than built through "Move to group" — that route is exercised (and
        // known flaky in a sandboxed run) elsewhere; this test is only about the toggle.
        using var app = AnchorApp.Launch(TwoChildGroupDockJson());
        var dock = app.WaitForDock();

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);
        var settings = app.WaitForWindow("Anchor Settings");
        AnchorApp.Press(AnchorApp.FindById(settings, "SettingsNavApps")!);

        // Expanded by default: both children show up as their own indented rows.
        Assert.True(AnchorApp.WaitUntil(() => RowExists(settings, "File Explorer")));
        Assert.True(RowExists(settings, "Notepad"));

        var collapse = AnchorApp.FindButtonByName(settings, "Collapse group");
        Assert.NotNull(collapse);
        AnchorApp.Press(collapse!);

        Assert.True(AnchorApp.WaitUntil(() => !RowExists(settings, "File Explorer") && !RowExists(settings, "Notepad")),
            "Collapsing the group did not hide both of its children's rows.");

        var expand = AnchorApp.FindButtonByName(settings, "Expand group");
        Assert.NotNull(expand);
        AnchorApp.Press(expand!);

        Assert.True(AnchorApp.WaitUntil(() => RowExists(settings, "File Explorer") && RowExists(settings, "Notepad")),
            "Expanding the group did not restore both of its children's rows.");

        settings.Close();
    }

    [UIFact]
    public void The_dock_survives_opening_and_closing_Settings()
    {
        // Closing a child window has taken the dock with it before now (the dock is the app's
        // only real window but is not its "main" one), so this guards that specifically.
        using var app = AnchorApp.Launch();
        var dock = app.WaitForDock();

        AnchorApp.Press(AnchorApp.FindById(dock, "AnchorSettingsButton")!);
        app.WaitForWindow("Anchor Settings").Close();
        Assert.True(app.WaitForWindowToClose("Anchor Settings"));

        Assert.False(app.Application.HasExited, "Anchor exited when its Settings window closed.");
        Assert.NotNull(app.WaitForDock());
    }
}
