namespace SpireMindMod;

internal static class EventOutcomeInterpreter
{
    internal static Dictionary<string, object?> BuildKnownOutcome(
        string eventId,
        string eventTypeName,
        string? textKey,
        string? title,
        string? description)
    {
        Dictionary<string, object?> knownOutcome = CreateEmptyOutcome();
        List<string> runtimeWarnings = new();
        string normalizedText = $"{eventId} {eventTypeName} {textKey} {title} {description}".ToUpperInvariant();
        string confidence = "low";
        string knownLevel = "unknown";

        bool handled = TryApplyCoreEvents(normalizedText, knownOutcome, runtimeWarnings, out confidence, out knownLevel)
            || EventOutcomeInterpreterClearEvents.TryApply(normalizedText, knownOutcome, runtimeWarnings, out confidence, out knownLevel)
            || EventOutcomeInterpreterVariedEvents.TryApply(normalizedText, knownOutcome, runtimeWarnings, out confidence, out knownLevel)
            || EventOutcomeInterpreterComplexEvents.TryApply(normalizedText, knownOutcome, runtimeWarnings, out confidence, out knownLevel);

        if (!handled)
        {
            knownOutcome["has_unknown_effects"] = true;
            runtimeWarnings.Add("unknown_event_outcome");
        }

        return new Dictionary<string, object?>
        {
            ["adapter_confidence"] = confidence,
            ["outcome_known_level"] = knownLevel,
            ["runtime_warnings"] = runtimeWarnings,
            ["known_outcome"] = knownOutcome
        };
    }

    internal static Dictionary<string, object?> CreateEmptyOutcome()
    {
        return new Dictionary<string, object?>
        {
            ["hp_delta"] = 0,
            ["max_hp_delta"] = 0,
            ["gold_delta"] = 0,
            ["remove_count"] = 0,
            ["upgrade_count"] = 0,
            ["transform_count"] = 0,
            ["card_reward_count"] = 0,
            ["potion_reward_count"] = 0,
            ["relic_ids"] = Array.Empty<string>(),
            ["potion_ids"] = Array.Empty<string>(),
            ["fixed_card_ids"] = Array.Empty<string>(),
            ["curse_card_ids"] = Array.Empty<string>(),
            ["starts_combat"] = false,
            ["has_randomness"] = false,
            ["has_unknown_effects"] = false,
            ["notes"] = Array.Empty<string>()
        };
    }

    internal static bool ContainsToken(string source, string token)
    {
        return source.Contains(token, StringComparison.Ordinal);
    }

    internal static bool ContainsEventName(string source, params string[] eventNames)
    {
        string compactSource = CompactEventName(source);
        return eventNames.Any(eventName =>
            source.Contains(eventName, StringComparison.Ordinal)
            || compactSource.Contains(CompactEventName(eventName), StringComparison.Ordinal));
    }

    private static string CompactEventName(string value)
    {
        return value
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    internal static void MarkRandom(List<string> runtimeWarnings)
    {
        runtimeWarnings.Add("random_reward");
    }

    internal static void MarkMultiStep(List<string> runtimeWarnings)
    {
        runtimeWarnings.Add("multi_step_event");
    }

    private static bool TryApplyCoreEvents(
        string normalizedText,
        Dictionary<string, object?> knownOutcome,
        List<string> runtimeWarnings,
        out string confidence,
        out string knownLevel)
    {
        confidence = "low";
        knownLevel = "unknown";

        if (ContainsEventName(normalizedText, "AROMAOFCHAOS", "AROMA_OF_CHAOS"))
        {
            if (ContainsToken(normalizedText, ".LET_GO"))
            {
                knownOutcome["transform_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (ContainsToken(normalizedText, ".MAINTAIN_CONTROL"))
            {
                knownOutcome["upgrade_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }
        else if (ContainsEventName(normalizedText, "DENSEVEGETATION", "DENSE_VEGETATION"))
        {
            if (ContainsToken(normalizedText, ".TRUDGE_ON"))
            {
                knownOutcome["remove_count"] = 1;
                knownOutcome["hp_delta"] = -11;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (ContainsToken(normalizedText, ".REST") || ContainsToken(normalizedText, ".FIGHT"))
            {
                knownOutcome["starts_combat"] = true;
                knownOutcome["has_unknown_effects"] = true;
                MarkMultiStep(runtimeWarnings);
                runtimeWarnings.Add("starts_combat");
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }
        }
        else if (ContainsEventName(normalizedText, "DROWNINGBEACON", "DROWNING_BEACON"))
        {
            if (ContainsToken(normalizedText, ".BOTTLE"))
            {
                knownOutcome["potion_reward_count"] = 1;
                knownOutcome["potion_ids"] = new[] { "GLOWWATER_POTION" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (ContainsToken(normalizedText, ".CLIMB"))
            {
                knownOutcome["max_hp_delta"] = -13;
                knownOutcome["relic_ids"] = new[] { "FRESNEL_LENS" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }
        else if (ContainsEventName(normalizedText, "WELLSPRING"))
        {
            if (ContainsToken(normalizedText, ".BOTTLE"))
            {
                knownOutcome["potion_reward_count"] = 1;
                knownOutcome["has_randomness"] = true;
                MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (ContainsToken(normalizedText, ".BATHE"))
            {
                knownOutcome["remove_count"] = 1;
                knownOutcome["curse_card_ids"] = new[] { "GUILTY" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }
        else if (ContainsEventName(normalizedText, "WHISPERINGHOLLOW", "WHISPERING_HOLLOW"))
        {
            if (ContainsToken(normalizedText, ".GOLD"))
            {
                knownOutcome["gold_delta"] = -50;
                knownOutcome["potion_reward_count"] = 2;
                knownOutcome["has_randomness"] = true;
                MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (ContainsToken(normalizedText, ".HUG"))
            {
                knownOutcome["transform_count"] = 1;
                knownOutcome["hp_delta"] = -9;
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }
        else if (ContainsEventName(normalizedText, "ZENWEAVER", "ZEN_WEAVER"))
        {
            if (ContainsToken(normalizedText, ".BREATHING_TECHNIQUES"))
            {
                knownOutcome["gold_delta"] = -50;
                knownOutcome["fixed_card_ids"] = new[] { "ENLIGHTENMENT", "ENLIGHTENMENT" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (ContainsToken(normalizedText, ".EMOTIONAL_AWARENESS"))
            {
                knownOutcome["gold_delta"] = -125;
                knownOutcome["remove_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (ContainsToken(normalizedText, ".ARACHNID_ACUPUNCTURE"))
            {
                knownOutcome["gold_delta"] = -250;
                knownOutcome["remove_count"] = 2;
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }

        return false;
    }
}
