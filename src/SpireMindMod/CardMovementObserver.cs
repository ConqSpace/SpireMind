using System.Reflection;
using System.Runtime.CompilerServices;

namespace SpireMindMod;

internal static class CardMovementObserver
{
    // 전투 진입 중 카드 이동 핵심 메서드 패치는 네이티브/엔진 생명주기와 충돌할 수 있습니다.
    // 안정성이 확인될 때까지 관찰 기능 전체를 기본 비활성화합니다.
    public static readonly bool Enabled = false;

    private static readonly SpireMindLogger Logger = new("SpireMind.R2.CardMove");
    private static readonly object Gate = new();
    private static readonly Dictionary<object, PileSnapshot> PreviousPiles = new(ReferenceEqualityComparer.Instance);
    private static readonly List<object> ObservedHandCards = new();
    private static readonly Dictionary<string, object> ObservedHandByRuntimeId = new(StringComparer.Ordinal);
    private static string? currentContextKey;

    public static void ObserveContext(object? combatRoot, object? player)
    {
        if (!Enabled)
        {
            return;
        }

        TrySafe("전투/턴 문맥 갱신", () =>
        {
            string nextContextKey = BuildContextKey(combatRoot, player);
            lock (Gate)
            {
                if (currentContextKey is null)
                {
                    currentContextKey = nextContextKey;
                    return;
                }

                if (!string.Equals(currentContextKey, nextContextKey, StringComparison.Ordinal))
                {
                    ClearLocked();
                    currentContextKey = nextContextKey;
                }
            }
        });
    }

    public static void ObserveRemoveFromCurrentPile(object? card)
    {
        if (!Enabled)
        {
            return;
        }

        TrySafe("카드 이전 더미 기록", () =>
        {
            if (card is null)
            {
                return;
            }

            PileSnapshot previousPile = ReadCurrentPile(card);
            lock (Gate)
            {
                PreviousPiles[card] = previousPile;
                if (previousPile.IsType("Hand"))
                {
                    RemoveFromObservedHandLocked(card);
                }
            }
        });
    }

    public static void ObserveAddInternal(object? pile, object? card)
    {
        if (!Enabled)
        {
            return;
        }

        TrySafe("카드 더미 추가 관찰", () =>
        {
            if (pile is null || card is null)
            {
                return;
            }

            PileSnapshot destinationPile = ReadPile(pile);
            lock (Gate)
            {
                PreviousPiles.TryGetValue(card, out PileSnapshot previousPile);
                PreviousPiles.Remove(card);

                if (destinationPile.IsType("Hand") && previousPile.IsType("Draw"))
                {
                    AddObservedHandLocked(card);
                    return;
                }

                if (!destinationPile.IsType("Hand"))
                {
                    RemoveFromObservedHandLocked(card);
                }
            }
        });
    }

    public static IReadOnlyList<object> GetObservedHandCards()
    {
        if (!Enabled)
        {
            return Array.Empty<object>();
        }

        lock (Gate)
        {
            return ObservedHandCards.ToArray();
        }
    }

    private static void AddObservedHandLocked(object card)
    {
        string runtimeId = BuildRuntimeId(card);
        if (ObservedHandCards.Any(existing => ReferenceEquals(existing, card)))
        {
            return;
        }

        if (ObservedHandByRuntimeId.ContainsKey(runtimeId))
        {
            return;
        }

        ObservedHandCards.Add(card);
        ObservedHandByRuntimeId[runtimeId] = card;
    }

    private static void RemoveFromObservedHandLocked(object card)
    {
        string runtimeId = BuildRuntimeId(card);
        ObservedHandCards.RemoveAll(existing =>
            ReferenceEquals(existing, card)
            || string.Equals(BuildRuntimeId(existing), runtimeId, StringComparison.Ordinal));
        ObservedHandByRuntimeId.Remove(runtimeId);
    }

    private static void ClearLocked()
    {
        PreviousPiles.Clear();
        ObservedHandCards.Clear();
        ObservedHandByRuntimeId.Clear();
    }

    private static string BuildContextKey(object? combatRoot, object? player)
    {
        object? combatState = FindMemberValue(player, "CombatState", "combatState", "_combatState")
            ?? FindMemberValue(combatRoot, "CombatState", "combatState", "_combatState")
            ?? combatRoot;
        int combatHash = combatState is null ? 0 : RuntimeHelpers.GetHashCode(combatState);
        object? round = FindMemberValue(combatState, "RoundNumber", "roundNumber", "_roundNumber", "TurnNumber", "turnNumber", "_turnNumber")
            ?? FindMemberValue(combatRoot, "RoundNumber", "roundNumber", "_roundNumber", "TurnNumber", "turnNumber", "_turnNumber");
        object? side = FindMemberValue(combatState, "CurrentSide", "currentSide", "_currentSide")
            ?? FindMemberValue(combatRoot, "CurrentSide", "currentSide", "_currentSide");
        return $"{combatHash}:{round?.ToString() ?? "unknown_round"}:{side?.ToString() ?? "unknown_side"}";
    }

    private static PileSnapshot ReadCurrentPile(object card)
    {
        object? pile = FindMemberValue(card, "Pile", "pile", "_pile", "CurrentPile", "currentPile", "_currentPile");
        return ReadPile(pile);
    }

    private static PileSnapshot ReadPile(object? pile)
    {
        if (pile is null)
        {
            return PileSnapshot.Empty;
        }

        object? type = FindMemberValue(pile, "Type", "type", "_type", "PileType", "pileType", "_pileType");
        return new PileSnapshot(pile, type?.ToString());
    }

    private static string BuildRuntimeId(object card)
    {
        string? id = ReadFirstString(
            card,
            "instanceId",
            "_instanceId",
            "instance_id",
            "_instance_id",
            "uuid",
            "_uuid",
            "guid",
            "_guid",
            "runtimeId",
            "_runtimeId",
            "entityId",
            "_entityId",
            "uniqueId",
            "_uniqueId");
        return string.IsNullOrWhiteSpace(id)
            ? $"ref:{RuntimeHelpers.GetHashCode(card)}"
            : $"id:{id}";
    }

    private static string? ReadFirstString(object? source, params string[] names)
    {
        object? value = FindMemberValue(source, names);
        return value?.ToString();
    }

    private static object? FindMemberValue(object? source, params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        foreach (MemberInfo member in GetReadableMembers(source.GetType()))
        {
            if (!names.Any(name => member.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            object? value = ReadMember(source, member);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<MemberInfo> GetReadableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in type.GetFields(flags))
        {
            yield return field;
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                yield return property;
            }
        }
    }

    private static object? ReadMember(object source, MemberInfo member)
    {
        try
        {
            return member switch
            {
                FieldInfo field => field.GetValue(source),
                PropertyInfo property => property.GetValue(source),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static void TrySafe(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Logger.Warning($"{operation} 실패. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    private readonly record struct PileSnapshot(object? Pile, string? TypeName)
    {
        public static PileSnapshot Empty => new(null, null);

        public bool IsType(string expected)
        {
            return string.Equals(TypeName, expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
