using System;
using System.Linq;
using System.Numerics;

namespace AXamlIDE.Services.NuGet;

/// <summary>
/// A NuGet package version ("1.2.3", "11.1.0.9782", "2.0.0-preview.3+build.7"), ordered the way NuGet orders them
/// (SemVer 2.0 plus a fourth number): numbers first, then a release sorts after any of its prereleases, and
/// prerelease labels compare part by part (numeric parts as numbers, below text parts). Build metadata is ignored.
/// </summary>
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
{
    private readonly BigInteger[] _numbers;      // always 4: major.minor.patch.revision
    private readonly string[] _releaseLabels;    // empty for a release

    private NuGetVersion(string original, BigInteger[] numbers, string[] releaseLabels)
    {
        Original = original;
        _numbers = numbers;
        _releaseLabels = releaseLabels;
    }

    /// <summary>The version as written (what's shown and passed to <c>dotnet add package --version</c>).</summary>
    public string Original { get; }

    public bool IsPrerelease => _releaseLabels.Length > 0;

    public static NuGetVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();

        var core = trimmed;
        var plus = core.IndexOf('+');
        if (plus >= 0)
            core = core[..plus];
        var dash = core.IndexOf('-');
        var release = dash >= 0 ? core[(dash + 1)..] : "";
        if (dash >= 0)
        {
            if (release.Length == 0)
                return null;   // "1.0.0-" has no label
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4)
            return null;
        var numbers = new BigInteger[4];
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !BigInteger.TryParse(parts[i], out numbers[i]))
                return null;

        var labels = release.Length == 0 ? [] : release.Split('.');
        if (labels.Any(label => label.Length == 0))
            return null;
        return new NuGetVersion(trimmed, numbers, labels);
    }

    public int CompareTo(NuGetVersion? other)
    {
        if (other is null)
            return 1;
        for (var i = 0; i < 4; i++)
            if (_numbers[i].CompareTo(other._numbers[i]) is var c and not 0)
                return c;

        // A release is newer than its prereleases.
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;

        for (var i = 0; i < Math.Min(_releaseLabels.Length, other._releaseLabels.Length); i++)
            if (CompareLabel(_releaseLabels[i], other._releaseLabels[i]) is var c and not 0)
                return c;
        return _releaseLabels.Length.CompareTo(other._releaseLabels.Length);
    }

    private static int CompareLabel(string a, string b)
    {
        var aNumeric = BigInteger.TryParse(a, out var aNumber) && a.All(char.IsAsciiDigit);
        var bNumeric = BigInteger.TryParse(b, out var bNumber) && b.All(char.IsAsciiDigit);
        return (aNumeric, bNumeric) switch
        {
            (true, true) => aNumber.CompareTo(bNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase)
        };
    }

    public bool Equals(NuGetVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is NuGetVersion other && Equals(other);
    public override int GetHashCode()
        => HashCode.Combine(_numbers[0], _numbers[1], _numbers[2], _numbers[3], string.Join('.', _releaseLabels).ToLowerInvariant());
    public override string ToString() => Original;

    public static bool operator <(NuGetVersion a, NuGetVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(NuGetVersion a, NuGetVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(NuGetVersion a, NuGetVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(NuGetVersion a, NuGetVersion b) => a.CompareTo(b) >= 0;
}
