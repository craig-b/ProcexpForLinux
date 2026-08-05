namespace Procexp.Privileged;

/// <summary>
/// Direct reads of <c>/etc/group</c> and <c>/etc/passwd</c>, shared by the
/// helper daemon and the client so both sides apply the same definition of
/// membership to the same lines. The files are read directly rather than
/// through NSS, matching the samplers (see docs/DISTROS.md) — users managed by
/// LDAP, sssd or systemd-homed are invisible here, so callers must treat "not
/// found" as "unknown" rather than "no".
/// </summary>
public static class GroupDatabase
{
    /// <summary>The gid and supplementary members of a group, or null if absent or unreadable.</summary>
    public static (uint Gid, string[] Members)? ReadGroup(string name)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/group"))
            {
                // name:password:gid:member,member
                var fields = line.Split(':');
                if (
                    fields.Length >= 4
                    && fields[0] == name
                    && uint.TryParse(fields[2], out var gid)
                )
                {
                    return (gid, fields[3].Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The user name for a uid, or null if absent or unreadable.</summary>
    public static string? ReadUserName(uint uid)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var fields = line.Split(':');
                if (
                    fields.Length >= 3
                    && uint.TryParse(fields[2], out var candidate)
                    && candidate == uid
                )
                {
                    return fields[0];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// The primary gid for a user name, or null if absent or unreadable.
    /// Primary membership lives in <c>/etc/passwd</c>, not in the group file's
    /// member list, so a membership test must consult both.
    /// </summary>
    public static uint? ReadPrimaryGid(string userName)
    {
        if (userName.Length == 0)
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                // name:password:uid:gid:...
                var fields = line.Split(':');
                if (
                    fields.Length >= 4
                    && fields[0] == userName
                    && uint.TryParse(fields[3], out var gid)
                )
                {
                    return gid;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
