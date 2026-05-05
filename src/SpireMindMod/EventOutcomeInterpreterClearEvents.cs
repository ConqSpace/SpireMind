namespace SpireMindMod;

internal static class EventOutcomeInterpreterClearEvents
{
    internal static bool TryApply(
        string normalizedText,
        Dictionary<string, object?> knownOutcome,
        List<string> runtimeWarnings,
        out string confidence,
        out string knownLevel)
    {
        confidence = "low";
        knownLevel = "unknown";

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "DOORS_OF_LIGHT_AND_DARK", "DOORSOFLIGHTANDDARK"))
        {
            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".LIGHT")
                || EventOutcomeInterpreter.ContainsToken(normalizedText, "LIGHT_DOOR")
                || EventOutcomeInterpreter.ContainsToken(normalizedText, "빛의 문"))
            {
                knownOutcome["upgrade_count"] = 2;
                knownOutcome["has_randomness"] = true;
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".DARK")
                || EventOutcomeInterpreter.ContainsToken(normalizedText, "DARK_DOOR")
                || EventOutcomeInterpreter.ContainsToken(normalizedText, "어둠의 문"))
            {
                knownOutcome["remove_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (IsProceedOrLeave(normalizedText))
            {
                knownOutcome["notes"] = new[] { "proceed or leave" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "WOODCARVINGS", "WOOD_CARVINGS"))
        {
            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".BIRD"))
            {
                knownOutcome["transform_count"] = 1;
                knownOutcome["fixed_card_ids"] = new[] { "PECK" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".SNAKE"))
            {
                knownOutcome["notes"] = new[] { "apply Slither enchantment to one card" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".TORUS"))
            {
                knownOutcome["transform_count"] = 1;
                knownOutcome["fixed_card_ids"] = new[] { "TORIC_TOUGHNESS" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "TABLETOFTRUTH", "TABLET_OF_TRUTH"))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(normalizedText, @"DECIPHER_([2-9]|\d{2,})"))
            {
                knownOutcome["upgrade_count"] = 1;
                knownOutcome["has_unknown_effects"] = true;
                runtimeWarnings.Add("state_dependent_outcome");
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "DECIPHER_1"))
            {
                knownOutcome["max_hp_delta"] = -6;
                knownOutcome["upgrade_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "DECIPHER"))
            {
                knownOutcome["max_hp_delta"] = -1;
                knownOutcome["upgrade_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "SMASH"))
            {
                knownOutcome["notes"] = new[] { "heal up to 12 hp" };
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "GIVE_UP"))
            {
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }

        return false;
    }

    private static bool IsProceedOrLeave(string normalizedText)
    {
        return EventOutcomeInterpreter.ContainsToken(normalizedText, "PROCEED")
            || EventOutcomeInterpreter.ContainsToken(normalizedText, "CONTINUE")
            || EventOutcomeInterpreter.ContainsToken(normalizedText, "LEAVE")
            || EventOutcomeInterpreter.ContainsToken(normalizedText, "DEPART");
    }
}
