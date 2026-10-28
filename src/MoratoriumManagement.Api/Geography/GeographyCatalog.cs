using System.Reflection;
using System.Text.RegularExpressions;
using MoratoriumManagement.Api.Domain;

namespace MoratoriumManagement.Api.Geography;

public static class UsStates
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["AL"] = "Alabama", ["AK"] = "Alaska", ["AZ"] = "Arizona", ["AR"] = "Arkansas", ["CA"] = "California",
        ["CO"] = "Colorado", ["CT"] = "Connecticut", ["DE"] = "Delaware", ["DC"] = "District of Columbia",
        ["FL"] = "Florida", ["GA"] = "Georgia", ["HI"] = "Hawaii", ["ID"] = "Idaho", ["IL"] = "Illinois",
        ["IN"] = "Indiana", ["IA"] = "Iowa", ["KS"] = "Kansas", ["KY"] = "Kentucky", ["LA"] = "Louisiana",
        ["ME"] = "Maine", ["MD"] = "Maryland", ["MA"] = "Massachusetts", ["MI"] = "Michigan", ["MN"] = "Minnesota",
        ["MS"] = "Mississippi", ["MO"] = "Missouri", ["MT"] = "Montana", ["NE"] = "Nebraska", ["NV"] = "Nevada",
        ["NH"] = "New Hampshire", ["NJ"] = "New Jersey", ["NM"] = "New Mexico", ["NY"] = "New York",
        ["NC"] = "North Carolina", ["ND"] = "North Dakota", ["OH"] = "Ohio", ["OK"] = "Oklahoma", ["OR"] = "Oregon",
        ["PA"] = "Pennsylvania", ["RI"] = "Rhode Island", ["SC"] = "South Carolina", ["SD"] = "South Dakota",
        ["TN"] = "Tennessee", ["TX"] = "Texas", ["UT"] = "Utah", ["VT"] = "Vermont", ["VA"] = "Virginia",
        ["WA"] = "Washington", ["WV"] = "West Virginia", ["WI"] = "Wisconsin", ["WY"] = "Wyoming",
        ["AS"] = "American Samoa", ["GU"] = "Guam", ["MP"] = "Northern Mariana Islands", ["PR"] = "Puerto Rico",
        ["VI"] = "U.S. Virgin Islands", ["UM"] = "U.S. Minor Outlying Islands",
    };
}

/// <summary>
/// In-memory index of valid states and state/county combinations. Loaded from the reference tables
/// at startup and rebuilt after county maintenance.
/// </summary>
public sealed class GeographyCatalog
{
    private static readonly string[] CountySuffixes =
        ["county", "parish", "borough", "census area", "municipality", "city and borough"];

    private sealed record StateIndex(Dictionary<string, string> KeyToCanonical, List<string> Canonical);

    private volatile Dictionary<string, StateIndex> _byState = new();
    private volatile Dictionary<string, string> _stateNames = new();

    public static string ShortNameOf(string officialName) =>
        officialName.EndsWith(" County", StringComparison.OrdinalIgnoreCase) ? officialName[..^7].Trim() : officialName.Trim();

    public static string Key(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    public void Load(IEnumerable<RefState> states, IEnumerable<RefCounty> counties)
    {
        var byState = new Dictionary<string, StateIndex>();
        foreach (var group in counties.GroupBy(c => c.State))
        {
            var keys = new Dictionary<string, string>();
            var list = group.OrderBy(c => c.ShortName, StringComparer.OrdinalIgnoreCase).ToList();
            // Official and canonical names win over looser suffix-stripped aliases.
            foreach (var c in list)
            {
                keys[Key(c.Name)] = c.ShortName;
                keys[Key(c.ShortName)] = c.ShortName;
            }
            foreach (var c in list)
            {
                foreach (var suffix in CountySuffixes)
                {
                    if (c.Name.EndsWith(" " + suffix, StringComparison.OrdinalIgnoreCase))
                        keys.TryAdd(Key(c.Name[..^(suffix.Length + 1)]), c.ShortName);
                }
            }
            byState[group.Key] = new StateIndex(keys, list.Select(c => c.ShortName).ToList());
        }
        _stateNames = states.ToDictionary(s => s.Code, s => s.Name);
        _byState = byState;
    }

    public IReadOnlyDictionary<string, string> States => _stateNames;

    /// <summary>Upper-cased code if it is a valid two-character state code, otherwise null.</summary>
    public string? NormalizeState(string? input)
    {
        var code = input?.Trim().ToUpperInvariant();
        return code is { Length: 2 } && _stateNames.ContainsKey(code) ? code : null;
    }

    /// <summary>Canonical county name for a county that belongs to the state, otherwise null.</summary>
    public string? ResolveCounty(string state, string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || !_byState.TryGetValue(state, out var idx)) return null;
        return idx.KeyToCanonical.GetValueOrDefault(Key(input));
    }

    public IReadOnlyList<string> Counties(string state) =>
        _byState.TryGetValue(state, out var idx) ? idx.Canonical : [];

    public static IEnumerable<RefCounty> ReadSeed()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("counties.csv"));
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('|');
            if (parts.Length != 2 || !UsStates.Names.ContainsKey(parts[0])) continue;
            yield return new RefCounty { State = parts[0], Name = parts[1], ShortName = ShortNameOf(parts[1]) };
        }
    }
}
