using System.Reflection;

namespace SpireMindMod;

internal static class RuntimePhaseResolver
{
    public static RuntimePhaseResolution Resolve()
    {
        object? combatManager = GetStaticPropertyValue("MegaCrit.Sts2.Core.Combat.CombatManager", "Instance");
        bool combatInProgress = ReadBool(combatManager, "IsInProgress") == true;
        bool combatPlayPhase = ReadBool(combatManager, "IsPlayPhase") == true;
        object? combatState = TryInvokeMethod(combatManager, "DebugOnlyGetState");

        if (combatInProgress && combatState is not null && !combatPlayPhase)
        {
            return new RuntimePhaseResolution(
                Phase: "unstable",
                Authority: "CombatManager.DebugOnlyGetState",
                IsStable: false,
                UnstableReason: "combat_in_progress_not_play_phase",
                CombatInProgress: true,
                CombatPlayPhase: false,
                CombatState: combatState,
                BlockScreenExportReason: "combat_in_progress_not_play_phase_blocks_screen_export");
        }

        if (combatInProgress && combatState is not null)
        {
            return new RuntimePhaseResolution(
                Phase: "combat_turn",
                Authority: "CombatManager.DebugOnlyGetState",
                IsStable: true,
                UnstableReason: null,
                CombatInProgress: true,
                CombatPlayPhase: combatPlayPhase,
                CombatState: combatState,
                BlockScreenExportReason: "combat_in_progress_blocks_screen_export");
        }

        if (combatInProgress)
        {
            return new RuntimePhaseResolution(
                Phase: "unstable",
                Authority: "CombatManager",
                IsStable: false,
                UnstableReason: "combat_in_progress_without_debug_state",
                CombatInProgress: true,
                CombatPlayPhase: combatPlayPhase,
                CombatState: null,
                BlockScreenExportReason: "combat_in_progress_without_debug_state_blocks_screen_export");
        }

        return new RuntimePhaseResolution(
            Phase: "screen_or_run",
            Authority: "screen_or_run_exporter",
            IsStable: true,
            UnstableReason: null,
            CombatInProgress: false,
            CombatPlayPhase: combatPlayPhase,
            CombatState: combatState,
            BlockScreenExportReason: null);
    }

    private static object? GetStaticPropertyValue(string typeName, string propertyName)
    {
        Type? type = FindType(typeName);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = type?.GetProperty(propertyName, flags);
        if (property is null)
        {
            return null;
        }

        try
        {
            return property.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static Type? FindType(string fullName)
    {
        Type? directType = Type.GetType(fullName);
        if (directType is not null)
        {
            return directType;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private static bool? ReadBool(object? source, string memberName)
    {
        object? value = FindMemberValue(source, memberName);
        return value is bool boolean ? boolean : null;
    }

    private static object? FindMemberValue(object? source, string memberName)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in source.GetType().GetFields(flags))
        {
            if (field.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadField(source, field);
            }
        }

        foreach (PropertyInfo property in source.GetType().GetProperties(flags))
        {
            if (property.GetIndexParameters().Length == 0
                && property.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase))
            {
                return ReadProperty(source, property);
            }
        }

        return null;
    }

    private static object? ReadField(object source, FieldInfo field)
    {
        try
        {
            return field.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadProperty(object source, PropertyInfo property)
    {
        try
        {
            return property.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryInvokeMethod(object? source, string methodName)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? method = source.GetType()
            .GetMethods(flags)
            .FirstOrDefault(candidate =>
                candidate.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && candidate.GetParameters().Length == 0);
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(source, null);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record RuntimePhaseResolution(
    string Phase,
    string Authority,
    bool IsStable,
    string? UnstableReason,
    bool CombatInProgress,
    bool CombatPlayPhase,
    object? CombatState,
    string? BlockScreenExportReason)
{
    public bool ShouldBlockScreenExport => CombatInProgress;

    public Dictionary<string, object?> ToDiagnostics(string? requestedPhase = null)
    {
        return new Dictionary<string, object?>
        {
            ["phase"] = Phase,
            ["requested_phase"] = requestedPhase,
            ["authority"] = Authority,
            ["is_stable"] = IsStable,
            ["unstable_reason"] = UnstableReason,
            ["combat_in_progress"] = CombatInProgress,
            ["combat_play_phase"] = CombatPlayPhase,
            ["has_combat_debug_state"] = CombatState is not null,
            ["block_screen_export_reason"] = BlockScreenExportReason
        };
    }
}
