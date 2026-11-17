using System.Diagnostics.CodeAnalysis;
using Unreal.Core.Contracts;
using Unreal.Core.Extensions;

namespace Unreal.Core.Models;

/// <summary>
/// Class to track all NetGuids being loaded during a replay.
/// </summary>
public class NetGuidCache
{
    //public Dictionary<uint, NetGuidCacheObject> ObjectLookup { get; private set; } = new Dictionary<uint, NetGuidCacheObject>();

    /// <summary>
    /// Maps net field export group name to the respective NetFieldExportGroup
    /// </summary>
    public Dictionary<string, NetFieldExportGroup> NetFieldExportGroupMap { get; private set; } = new Dictionary<string, NetFieldExportGroup>();

    /// <summary>
    /// Maps assigned net field export group index to the respective FNetFieldExportGroup name.
    /// </summary>
    public Dictionary<uint, string> NetFieldExportGroupIndexToGroup { get; private set; } = new Dictionary<uint, string>();

    /// <summary>
    /// Maps netguid to the respective FNetFieldExportGroup name.
    /// </summary>
    public Dictionary<uint, string> NetGuidToPathName { get; private set; } = new Dictionary<uint, string>();

    /// <summary>
    /// Maps assigned net field export group index to the respective FNetFieldExportGroup name.
    /// </summary>
    public Dictionary<uint, NetFieldExportGroup> NetFieldExportGroupMapPathFixed { get; private set; } = new Dictionary<uint, NetFieldExportGroup>();

    /// <summary>
    /// Holds data about the tag dictionary
    /// </summary>
    public NetFieldExportGroup? NetworkGameplayTagNodeIndex
    {
        get
        {
            if (_networkGameplayTagNodeIndex == null)
            {
                // bUseFastReplication
                if (NetFieldExportGroupMap.TryGetValue("NetworkGameplayTagNodeIndex", out var nodeIndex))
                {
                    _networkGameplayTagNodeIndex = nodeIndex;
                }
                // bUseDynamicReplication
                else if (NetFieldExportGroupMap.TryGetValue("NetworkGameplayTagDynamicIndex", out nodeIndex))
                {
                    _networkGameplayTagNodeIndex = nodeIndex;
                }
            }
            return _networkGameplayTagNodeIndex;
        }
    }

    /// <summary>
    /// Maps a netguid to received <see cref="IExternalData"/>.
    /// </summary>
    public Dictionary<uint, IExternalData> ExternalData { get; private set; } = new();

    private readonly Dictionary<uint, NetFieldExportGroup> _netFieldExportGroupFromIndex = new();
    private readonly Dictionary<uint, NetFieldExportGroup> _archTypeToExportGroup = new();
    private readonly Dictionary<uint, string> _cleanedPaths = new();
    private readonly Dictionary<string, string> _cleanedClassNetCache = new();
    private readonly HashSet<string> _failedPaths = new(); //Path names that didn't find an export group
    private NetFieldExportGroup? _networkGameplayTagNodeIndex { get; set; }

    /// <summary>
    /// Clears export group mappings.
    /// </summary>
    public void ClearExportGroups()
    {
        NetFieldExportGroupMap.Clear();
        NetFieldExportGroupIndexToGroup.Clear();
        _netFieldExportGroupFromIndex.Clear();
        NetFieldExportGroupMapPathFixed.Clear();
        _archTypeToExportGroup.Clear();
        _cleanedPaths.Clear();
        _cleanedClassNetCache.Clear();
        _failedPaths.Clear();
        _networkGameplayTagNodeIndex = null;
    }

    /// <summary>
    /// Add a <see cref="NetFieldExportGroup"/> to the GuidCache.
    /// </summary>
    public void AddToExportGroupMap(string group, NetFieldExportGroup exportGroup)
    {
        if (group.EndsWith("ClassNetCache"))
        {
            exportGroup.PathName = exportGroup.PathName.RemoveAllPathPrefixes();
        }

        if (!NetFieldExportGroupMap.TryGetValue(group, out var previous))
        {
            // An actor can arrive before its class's export table. Retry failed
            // resolutions when another table becomes available.
            _failedPaths.Clear();
            // A legacy prefix match can also have selected a base class while
            // the exact subclass table was missing. Evict only actors that the
            // new exact class name can resolve, preserving unrelated mappings.
            var className = group.RemoveAllPathPrefixes();
            List<uint>? retryActors = null;
            foreach (var cached in NetFieldExportGroupMapPathFixed)
            {
                var actor = cached.Key;
                // A new package can introduce the same class leaf. Previously
                // matched numbered instances must then be retried too, so the
                // exact matcher can reject the now ambiguous layout.
                if (cached.Value.PathName.RemoveAllPathPrefixes() == className)
                {
                    (retryActors ??= []).Add(actor);
                    continue;
                }
                if (!NetGuidToPathName.TryGetValue(actor, out var actorPath)) continue;
                var name = actorPath.RemoveAllPathPrefixes();
                if (name == className || className.EndsWith("_C", StringComparison.Ordinal) &&
                    TryGetInstanceBaseName(name, out var instanceName) &&
                    (instanceName == className || instanceName + "_C" == className))
                    (retryActors ??= []).Add(actor);
            }
            if (retryActors != null)
                foreach (var actor in retryActors)
                {
                    NetFieldExportGroupMapPathFixed.Remove(actor);
                    _archTypeToExportGroup.Remove(actor);
                }
        }
        else if (!ReferenceEquals(previous, exportGroup))
        {
            // Checkpoint tables and replacement groups must not reuse an earlier
            // actor mapping or a normalized name from the old table index.
            NetFieldExportGroupMapPathFixed.Clear();
            _archTypeToExportGroup.Clear();
            _cleanedPaths.Clear();
            _failedPaths.Clear();
            _networkGameplayTagNodeIndex = null;
            if (previous.PathNameIndex != exportGroup.PathNameIndex)
            {
                NetFieldExportGroupIndexToGroup.Remove(previous.PathNameIndex);
                _netFieldExportGroupFromIndex.Remove(previous.PathNameIndex);
            }
        }

        NetFieldExportGroupMap[group] = exportGroup;
        NetFieldExportGroupIndexToGroup[exportGroup.PathNameIndex] = group;
        _netFieldExportGroupFromIndex[exportGroup.PathNameIndex] = exportGroup;
    }

    /// <summary>
    /// Get the <see cref="NetFieldExportGroup"/> by the index.
    /// </summary>
    /// <param name="index"></param>
    /// <returns><see cref="NetFieldExportGroup"/></returns>
    public NetFieldExportGroup? GetNetFieldExportGroupFromIndex(uint? index)
    {
        if (index is null)
        {
            return null;
        }

        if (_netFieldExportGroupFromIndex.TryGetValue(index.Value, out var group))
        {
            return group;
        }

        if (NetFieldExportGroupIndexToGroup.TryGetValue(index.Value, out var groupName) &&
            NetFieldExportGroupMap.TryGetValue(groupName, out group))
        {
            _netFieldExportGroupFromIndex[index.Value] = group;
            return group;
        }

        return null;
    }

    /// <summary>
    /// Get the <see cref="NetFieldExportGroup"/> by the path.
    /// </summary>
    /// <param name="path"></param>
    /// <returns><see cref="NetFieldExportGroup"/></returns>
    public NetFieldExportGroup? GetNetFieldExportGroup(string path)
    {
        if (string.IsNullOrEmpty(path) || !NetFieldExportGroupMap.TryGetValue(path, out var group))
        {
            return null;
        }
        return group;
    }

    /// <summary>
    /// Get the <see cref="NetFieldExportGroup"/> by the Actor guid.
    /// </summary>
    /// <param name="netguid"></param>
    /// <returns><see cref="NetFieldExportGroup"/></returns>
    public NetFieldExportGroup? GetNetFieldExportGroup(uint? netguid)
    {
        if (!netguid.HasValue)
        {
            return null;
        }

        if (!_archTypeToExportGroup.TryGetValue(netguid.Value, out var group))
        {
            if (!NetGuidToPathName.TryGetValue(netguid.Value, out var path))
            {
                return null;
            }

            // Failed resolutions remain cached until the export tables change.
            if (_failedPaths.Contains(path))
            {
                return null;
            }

            if (NetFieldExportGroupMapPathFixed.TryGetValue(netguid.Value, out group))
            {
                _archTypeToExportGroup[netguid.Value] = NetFieldExportGroupMapPathFixed[netguid.Value];
                return group;
            }

            // A script subclass such as FortAbilitySystemComponentAthena must
            // select its own table before the legacy substring lookup can match
            // the shorter FortAbilitySystemComponent name.
            if (NetFieldExportGroupMap.TryGetValue(path, out group) ||
                MatchExactGroupName(path.RemoveAllPathPrefixes(), out group))
            {
                if (group != null)
                {
                    NetFieldExportGroupMapPathFixed[netguid.Value] = group;
                    _archTypeToExportGroup[netguid.Value] = group;
                }
                else _failedPaths.Add(path);
                return group;
            }

            if (TryGetInstanceBaseName(path, out var instanceName))
            {
                // Placed instances can omit _C and include a UAID or hash. Match
                // their full class name before trimming instance numbers; a
                // substring match could otherwise select a different chest type.
                var className = instanceName.EndsWith("_C", StringComparison.Ordinal)
                    ? instanceName : instanceName + "_C";
                var matched = MatchExactGroupName(className, out group);
                if (!matched && className != instanceName)
                    matched = MatchExactGroupName(instanceName, out group);
                var numberedBase = instanceName.CleanPathSuffix();
                if (!matched && numberedBase != instanceName)
                    matched = MatchExactGroupName(numberedBase + "_C", out group) ||
                        MatchExactGroupName(numberedBase, out group);
                if (group != null)
                {
                    NetFieldExportGroupMapPathFixed[netguid.Value] = group;
                    _archTypeToExportGroup[netguid.Value] = group;
                    return group;
                }

                // Unknown or ambiguous instance classes stay unresolved. Do not
                // fall through to the legacy substring matching below.
                _failedPaths.Add(path);
                return null;
            }

            foreach (var groupPathKvp in NetFieldExportGroupMap)
            {
                var groupPath = groupPathKvp.Key;

                if (!_cleanedPaths.TryGetValue(groupPathKvp.Value.PathNameIndex, out var groupPathFixed))
                {
                    groupPathFixed = groupPath.RemoveAllPathPrefixes();
                    _cleanedPaths[groupPathKvp.Value.PathNameIndex] = groupPathFixed;
                }

                if (path.Contains(groupPathFixed, StringComparison.Ordinal))
                {
                    NetFieldExportGroupMapPathFixed[netguid.Value] = NetFieldExportGroupMap[groupPath];
                    _archTypeToExportGroup[netguid.Value] = NetFieldExportGroupMap[groupPath];

                    return NetFieldExportGroupMap[groupPath];
                }
            }

            var cleanedPath = path.CleanPathSuffix();
            foreach (var groupPathKvp in NetFieldExportGroupMap)
            {
                var groupPath = groupPathKvp.Key;

                if (_cleanedPaths.TryGetValue(groupPathKvp.Value.PathNameIndex, out var groupPathFixed))
                {
                    if (groupPathFixed.Contains(cleanedPath, StringComparison.Ordinal))
                    {
                        NetFieldExportGroupMapPathFixed[netguid.Value] = NetFieldExportGroupMap[groupPath];
                        _archTypeToExportGroup[netguid.Value] = NetFieldExportGroupMap[groupPath];

                        return NetFieldExportGroupMap[groupPath];
                    }
                }
            }

            _failedPaths.Add(path);
            return null;
        }

        return group;
    }

    // A matched but ambiguous name returns true with no group, preventing a
    // fallback to a shorter name and an unrelated class.
    private bool MatchExactGroupName(string className, out NetFieldExportGroup? result)
    {
        result = null;
        foreach (var pair in NetFieldExportGroupMap)
        {
            if (!_cleanedPaths.TryGetValue(pair.Value.PathNameIndex, out var name))
            {
                name = pair.Key.RemoveAllPathPrefixes();
                _cleanedPaths[pair.Value.PathNameIndex] = name;
            }
            if (name != className) continue;
            // A leaf name shared by several packages does not establish which
            // layout the instance uses.
            if (result != null) { result = null; return true; }
            result = pair.Value;
        }
        return result != null;
    }

    private static bool TryGetInstanceBaseName(string path, out string name)
    {
        name = path.RemoveAllPathPrefixes();
        var uaid = name.IndexOf("_UAID_", StringComparison.Ordinal);
        if (uaid > 0)
        {
            name = name[..uaid];
            return true;
        }
        if (name.Length == 0 || !char.IsAsciiDigit(name[^1])) return false;
        var separator = name.LastIndexOf('_');
        if (separator >= 17 && name.AsSpan(separator + 1).IndexOfAnyExceptInRange('0', '9') < 0 &&
            name[separator - 17] == '_' && IsInstanceHash(name.AsSpan(separator - 16, 16)))
        {
            name = name[..(separator - 17)];
        }
        // Without a hash, keep the number for the first exact lookup: it may be
        // part of a real class name rather than an instance suffix.
        return true;
    }

    private static bool IsInstanceHash(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }

    /// <summary>
    /// Tries to find the ClassNetCache for the given group path.
    /// </summary>
    /// <param name="group"></param>
    /// <returns>true if ClassNetCache was found, false otherwise</returns>
    public bool TryGetClassNetCache(string? group, [NotNullWhen(returnValue: true)] out NetFieldExportGroup? netFieldExportGroup, bool useFullName)
    {
        if (string.IsNullOrEmpty(group))
        {
            netFieldExportGroup = null;
            return false;
        }

        if (!_cleanedClassNetCache.TryGetValue(group, out var classNetCachePath))
        {
            classNetCachePath = useFullName ? $"{group}_ClassNetCache" : $"{group.RemoveAllPathPrefixes()}_ClassNetCache";
            _cleanedClassNetCache[group] = classNetCachePath;
        }

        return NetFieldExportGroupMap.TryGetValue(classNetCachePath, out netFieldExportGroup);
    }

    /// <summary>
    /// Tries to resolve the netguid using the <see cref="NetGuidToPathName"/>
    /// </summary>
    /// <param name="netguid"></param>
    /// <param name="pathName"></param>
    /// <returns>true if netguid was resolved, false otherwise</returns>
    public bool TryGetPathName(uint netguid, [NotNullWhen(returnValue: true)] out string pathName) => NetGuidToPathName.TryGetValue(netguid, out pathName);

    /// <summary>
    /// Tries to resolve the tagIndex using the <see cref="NetworkGameplayTagNodeIndex"/>
    /// </summary>
    /// <param name="tagIndex"></param>
    /// <param name="tagName"></param>
    /// <returns>true if tag was resolved, false otherwise</returns>
    public bool TryGetTagName(uint tagIndex, [NotNullWhen(returnValue: true)] out string tagName)
    {
        tagName = "";
        if (NetworkGameplayTagNodeIndex != null && NetworkGameplayTagNodeIndex.IsValidIndex(tagIndex))
        {
            if (NetworkGameplayTagNodeIndex.NetFieldExports[tagIndex] != null)
            {
                tagName = NetworkGameplayTagNodeIndex.NetFieldExports[tagIndex].Name;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tries to resolve the netguid using the <see cref="NetGuidToPathName"/>
    /// </summary>
    /// <param name="netguid"></param>
    /// <param name="pathName"></param>
    /// <returns>true if netguid was resolved, false otherwise</returns>
    public bool TryGetExternalData(uint? netguid, [NotNullWhen(returnValue: true)] out IExternalData? externalData)
    {
        externalData = null;
        if (netguid != null && ExternalData.ContainsKey(netguid.Value))
        {
            return ExternalData.Remove(netguid.Value, out externalData);
        }
        return false;
    }


    /// <summary>
    /// Empty the NetGuidCache
    /// </summary>
    public void Cleanup()
    {
        ClearExportGroups();
        NetGuidToPathName.Clear();
        //ObjectLookup.Clear();
        ExternalData.Clear();
    }
}
