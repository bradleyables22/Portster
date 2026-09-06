using System.Text.Json;
using System.Text.RegularExpressions;

namespace Portster;

public sealed class ProfileStore(StoragePaths paths, ServerPolicy policy)
{
    public ProfileSummary[] List(ProfileScope scope)
    {
        var directory = paths.Profiles(scope);
        if (!Directory.Exists(directory))
        {
            return [];
        }
        using var lease = FileLease.Acquire(directory);
        var files = Directory.EnumerateFiles(directory, "*.json").Take(policy.MaxProfiles + 1).ToArray();
        if (files.Length > policy.MaxProfiles)
        {
            throw new PortsterException("PROFILE_LIMIT", "The stored profile count exceeds the configured limit. No partial catalog was returned.");
        }
        return files
            .Order(StringComparer.Ordinal)
            .Select(path => ReadProfileFile(path))
            .Select(profile => new ProfileSummary(profile.Id, profile.DisplayName, profile.Revision, scope))
            .ToArray();
    }

    public DeviceProfile Get(string id, ProfileScope scope)
    {
        var path = GetProfilePath(id, scope);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new PortsterException("PROFILE_NOT_FOUND", "No profile exists with this ID in the selected scope.");
        }
        using var lease = FileLease.Acquire(Path.GetDirectoryName(path)!);
        return ReadProfileFile(path);
    }

    private DeviceProfile ReadProfileFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new PortsterException("PROFILE_NOT_FOUND", "No profile exists with this ID in the selected scope.");
        }
        if (new FileInfo(path).Length > policy.MaxProfileBytes)
        {
            throw new PortsterException("PROFILE_TOO_LARGE", "The profile exceeds the configured storage limit.");
        }
        try
        {
            var profile = JsonSerializer.Deserialize<DeviceProfile>(File.ReadAllText(path), Json.Options)
                ?? throw new JsonException();
            Validate(profile, fromTool: false);
            if (profile.Id != Path.GetFileNameWithoutExtension(path))
            {
                throw new PortsterException("INVALID_PROFILE", "Profile ID does not match its filename.");
            }
            return profile;
        }
        catch (JsonException)
        {
            throw new PortsterException(
                "INVALID_PROFILE",
                "Profile JSON is malformed or has unsupported properties. No migration is available for unknown schemas.");
        }
    }

    public DeviceProfile Save(DeviceProfile profile, ProfileScope scope, int? expectedRevision)
    {
        Validate(profile, fromTool: true);
        var path = GetProfilePath(profile.Id, scope);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        using var lease = FileLease.Acquire(directory);
        DeviceProfile? previous = null;
        if (expectedRevision is null)
        {
            if (File.Exists(path))
            {
                throw new PortsterException("PROFILE_EXISTS", "Profile already exists; use profile_update with its current revision.");
            }
            if (Directory.EnumerateFiles(directory, "*.json").Take(policy.MaxProfiles).Count() >= policy.MaxProfiles)
            {
                throw new PortsterException("PROFILE_LIMIT", "The profile count limit has been reached.");
            }
        }
        else
        {
            previous = ReadProfileFile(path);
            if (previous.Revision != expectedRevision)
            {
                throw new PortsterException("REVISION_CONFLICT", "The profile changed. Read it again before updating.");
            }
            // An operator-confirmed profile can only be changed offline.
            if (previous.Knowledge.Any(k => k.Confirmed))
            {
                throw new PortsterException(
                    "CONFIRMED_PROFILE",
                    "This profile contains operator-confirmed facts. Edit it offline to preserve or revise those confirmations.");
            }
        }
        var now = DateTimeOffset.UtcNow;
        var saved = profile with
        {
            Revision = checked((previous?.Revision ?? 0) + 1),
            CreatedUtc = previous?.CreatedUtc ?? now,
            UpdatedUtc = now
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(saved, Json.Options);
        if (bytes.Length > policy.MaxProfileBytes)
        {
            throw new PortsterException("PROFILE_TOO_LARGE", "The serialized profile exceeds the configured storage limit.");
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: expectedRevision is not null);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return saved;
    }

    public Done Delete(string id, ProfileScope scope, int expectedRevision)
    {
        var path = GetProfilePath(id, scope);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new PortsterException("PROFILE_NOT_FOUND", "No profile exists in the selected scope.");
        }
        using var lease = FileLease.Acquire(Path.GetDirectoryName(path)!);
        if (ReadProfileFile(path).Revision != expectedRevision)
        {
            throw new PortsterException("REVISION_CONFLICT", "The profile changed. Read it again before deleting.");
        }
        File.Delete(path);
        return new();
    }

    private string GetProfilePath(string id, ProfileScope scope)
    {
        if (id is null || !Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9_-]{0,63}\z"))
        {
            throw new PortsterException(
                "INVALID_PROFILE_ID",
                "Use a lowercase ID of 1..64 letters, digits, hyphens, or underscores, beginning with a letter or digit.");
        }
        return Path.Combine(paths.Profiles(scope), id + ".json");
    }

    public ProfileValidation Validate(DeviceProfile profile, bool fromTool = true)
    {
        if (profile is null)
        {
            throw new PortsterException("INVALID_PROFILE", "A profile object is required.");
        }
        _ = GetProfilePath(profile.Id, ProfileScope.Global);
        ValidateFields(profile);
        profile.Settings.Validate();
        profile.Framing.Validate(policy);
        ValidateSelector(profile.Selector);
        ValidateSafety(profile.Safety);
        ValidateKnowledge(profile.Knowledge, fromTool);
        ValidateSecretReferences(profile.SecretReferences);

        var warnings = new List<string> { "USB adapter matching does not authenticate the attached UART target. Protocol knowledge is descriptive and is never executed." };
        if (profile.Selector.UsbSerialNumber is null)
        {
            warnings.Add("This selector has no USB serial number. A missing or ambiguous match will be rejected.");
        }
        if (profile.Knowledge.Any(fact => !fact.Confirmed))
        {
            warnings.Add("Unconfirmed knowledge is draft information and grants no permission to act.");
        }
        return new(true, warnings.ToArray());
    }

    private static void ValidateFields(DeviceProfile profile)
    {
        if (profile.SchemaVersion != 1)
        {
            throw new PortsterException(
                "UNSUPPORTED_PROFILE_VERSION",
                "Only profile schema version 1 is supported. Newer versions are never silently downgraded.");
        }
        if (profile.Revision < 0 ||
            string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 128 ||
            profile.TargetDescription?.Length > 2048 ||
            profile.Selector is null || profile.Settings is null ||
            profile.Framing is null || profile.Safety is null ||
            profile.Knowledge is null || profile.Knowledge.Length > 128 ||
            profile.SecretReferences is null || profile.SecretReferences.Count > 32)
        {
            throw new PortsterException("INVALID_PROFILE", "Profile fields are missing or exceed their length/count limits.");
        }
    }

    private void ValidateSafety(ProfileSafety safety)
    {
        if (safety.MaxWriteBytes < 1 || safety.MaxWriteBytes > policy.MaxWriteBytes ||
            safety.MinWriteIntervalMs < policy.MinWriteIntervalMs || safety.MinWriteIntervalMs > 60000 ||
            safety.AllowSignals && !policy.AllowSignals || safety.AllowWrites && !policy.AllowWrites)
        {
            throw new PortsterException("POLICY_DENIED", "Profile permissions and write limits must stay within the operator's server policy.");
        }
    }

    private static void ValidateKnowledge(KnowledgeFact[] knowledge, bool fromTool)
    {
        foreach (var fact in knowledge)
        {
            if (fact is null ||
                string.IsNullOrWhiteSpace(fact.Name) || fact.Name.Length > 128 ||
                fact.Value is null || fact.Value.Length > 8192 ||
                fact.Reference?.Length > 2048 || !Enum.IsDefined(fact.Source))
            {
                throw new PortsterException("INVALID_KNOWLEDGE", "Knowledge facts must have bounded names, values, references, and valid provenance.");
            }
            if (fromTool && (fact.Confirmed || fact.ValidatedUtc.HasValue))
            {
                throw new PortsterException(
                    "CONFIRMATION_NOT_ALLOWED",
                    "Tools can save draft facts only. Confirmation and validation timestamps require an offline operator edit.");
            }
            if (fact.Source == KnowledgeSource.Inferred && fact.Confirmed)
            {
                throw new PortsterException("INVALID_KNOWLEDGE", "An inference cannot be marked confirmed; establish a documented or observed basis first.");
            }
        }
    }

    private static void ValidateSecretReferences(Dictionary<string, string> references)
    {
        foreach (var pair in references)
        {
            if (pair.Key.Length is < 1 or > 64 || pair.Value is null || !Regex.IsMatch(pair.Value, @"\Aenv:[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
            {
                throw new PortsterException(
                    "INVALID_SECRET_REFERENCE",
                    "Only env:VARIABLE_NAME secret references are supported; credentials must not be embedded.");
            }
        }
    }

    public static void ValidateSelector(DeviceSelector selector)
    {
        if (selector is null)
        {
            throw new PortsterException("INVALID_SELECTOR", "A selector is required.");
        }
        if ((selector.Vid is null) != (selector.Pid is null) ||
            selector.Vid is not null && !Regex.IsMatch(selector.Vid, @"\A[0-9a-fA-F]{4}\z") ||
            selector.Pid is not null && !Regex.IsMatch(selector.Pid, @"\A[0-9a-fA-F]{4}\z") ||
            selector.InterfaceNumber is not null && !Regex.IsMatch(selector.InterfaceNumber, @"\A[0-9a-fA-F]{2}\z") ||
            selector.UsbSerialNumber is not null && (string.IsNullOrWhiteSpace(selector.UsbSerialNumber) || selector.UsbSerialNumber.Length > 256) ||
            selector.LastSeenPort is not null && !ServerPolicy.IsPortName(selector.LastSeenPort) ||
            selector.Vid is null && selector.UsbSerialNumber is null && selector.LastSeenPort is null)
        {
            throw new PortsterException(
                "INVALID_SELECTOR",
                "Provide VID/PID together, a USB serial number, or an explicit lastSeenPort. IDs must be hexadecimal.");
        }
    }

    public static PortInfo Resolve(DeviceSelector selector, IReadOnlyList<PortInfo> ports)
    {
        ValidateSelector(selector);
        bool MatchesField(string? expected, string? actual) => expected is null || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        var hasUsbIdentity = selector.Vid is not null || selector.UsbSerialNumber is not null || selector.InterfaceNumber is not null;
        var candidates = ports.Where(p => MatchesField(selector.Vid, p.Vid) && MatchesField(selector.Pid, p.Pid) &&
            MatchesField(selector.UsbSerialNumber, p.UsbSerialNumber) && MatchesField(selector.InterfaceNumber, p.InterfaceNumber) &&
            (hasUsbIdentity || MatchesField(selector.LastSeenPort, p.PortName))).ToArray();
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new PortsterException("DEVICE_NOT_FOUND", "No device matches the selector. A last-seen port never overrides a failed USB identity match."),
            _ => throw new PortsterException("AMBIGUOUS_DEVICE", "Multiple devices match. Add a USB serial number/interface or explicitly open a chosen port.")
        };
    }
}
