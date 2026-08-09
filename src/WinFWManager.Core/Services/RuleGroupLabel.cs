namespace WinFWManager.Core.Services;

/// <summary>
/// Makes a firewall rule's group readable.
///
/// Most groups arrive already resolved, because the WMI provider turns the classic
/// "@FirewallAPI.dll,-32752" form into DisplayGroup ("Network Discovery"). UWP packages
/// use a different form the provider leaves alone:
///
///   @{MicrosoftWindows.LKG.IrisService_1000.26100.1742.0_x64__cw5n1h2txyewy?ms-resource://…}
///
/// SHLoadIndirectString cannot resolve those either — verified against real values on a
/// test machine, where they came back unchanged because the packages are not registered
/// for the current user, so there is no string to load. The package name is still in the
/// reference though, so it is extracted rather than showing a resource URI.
/// </summary>
public static class RuleGroupLabel
{
    public static string Humanize(string? group)
    {
        if (string.IsNullOrEmpty(group) || group[0] != '@')
            return group ?? "";

        // "@{PackageFullName?ms-resource://...}" — take the package name, which is the
        // part of the full name before the version suffix.
        if (group.Length > 2 && group[1] == '{')
        {
            var end = group.IndexOf('?');
            if (end > 2)
            {
                var packageFullName = group[2..end];
                var underscore = packageFullName.IndexOf('_');
                var name = underscore > 0 ? packageFullName[..underscore] : packageFullName;

                if (name.Length > 0)
                    return name;
            }
        }

        // Anything else (an unresolved DLL resource) is left as-is: inventing a label
        // would hide that the group could not be resolved.
        return group;
    }
}
