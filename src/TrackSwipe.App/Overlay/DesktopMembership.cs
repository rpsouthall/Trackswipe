namespace TrackSwipe.Overlay;

/// <summary>Visibility is not desktop ownership. Prefer the shell's desktop ID whenever available.</summary>
internal static class DesktopMembership
{
    public static int Resolve(int cloak, int current, IReadOnlyList<Guid>? desktops, Guid? id)
    {
        const int appCloaked = 1, shellCloaked = 2, inheritedCloak = 4;
        if (cloak < 0 || (cloak & appCloaked) != 0 || (cloak != 0 && (cloak & shellCloaked) == 0)
            || (cloak & ~(appCloaked | shellCloaked | inheritedCloak)) != 0) return -1;

        if (desktops is not null && id is { } found && found != Guid.Empty)
        {
            for (var i = 0; i < desktops.Count; i++)
                if (desktops[i] == found) return i;
            return -1; // a stale ID must not get relabelled as the current desktop
        }
        return cloak == 0 ? current : -1;
    }
}
