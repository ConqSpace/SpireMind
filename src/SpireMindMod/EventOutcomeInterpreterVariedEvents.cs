namespace SpireMindMod;

internal static class EventOutcomeInterpreterVariedEvents
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

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "ENDLESSCONVEYOR", "ENDLESS_CONVEYOR"))
        {
            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".LEAVE"))
            {
                knownOutcome["notes"] = new[] { "leave" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".OBSERVE_CHEF")
                || EventOutcomeInterpreter.ContainsToken(normalizedText, ".SPICY_SNAPPY"))
            {
                knownOutcome["upgrade_count"] = 1;
                knownOutcome["has_randomness"] = true;
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".CAVIAR"))
            {
                knownOutcome["gold_delta"] = -35;
                knownOutcome["max_hp_delta"] = 4;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".CLAM_ROLL"))
            {
                knownOutcome["gold_delta"] = -35;
                knownOutcome["notes"] = new[] { "heal_amount_depends_on_current_state" };
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".JELLY_LIVER"))
            {
                knownOutcome["gold_delta"] = -35;
                knownOutcome["transform_count"] = 1;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".FRIED_EEL"))
            {
                knownOutcome["gold_delta"] = -35;
                knownOutcome["card_reward_count"] = 1;
                knownOutcome["has_randomness"] = true;
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".SUSPICIOUS_CONDIMENT"))
            {
                knownOutcome["gold_delta"] = -35;
                knownOutcome["potion_reward_count"] = 1;
                knownOutcome["has_randomness"] = true;
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".GOLDEN_FYSH"))
            {
                knownOutcome["gold_delta"] = 75;
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".SEAPUNK_SALAD"))
            {
                knownOutcome["gold_delta"] = -35;
                knownOutcome["fixed_card_ids"] = new[] { "FEEDING_FRENZY" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            knownOutcome["has_randomness"] = true;
            knownOutcome["has_unknown_effects"] = true;
            runtimeWarnings.Add("unknown_event_outcome");
            confidence = "low";
            knownLevel = "unknown";
            return true;
        }

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "DOLLROOM", "DOLL_ROOM"))
        {
            List<string> visibleRelicIds = new();

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "DAUGHTER_OF_THE_WIND"))
            {
                visibleRelicIds.Add("DAUGHTER_OF_THE_WIND");
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "MR_STRUGGLES"))
            {
                visibleRelicIds.Add("MR_STRUGGLES");
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, "BING_BONG"))
            {
                visibleRelicIds.Add("BING_BONG");
            }

            if (visibleRelicIds.Count > 0)
            {
                knownOutcome["relic_ids"] = visibleRelicIds.ToArray();
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".INITIAL.OPTIONS.RANDOM"))
            {
                knownOutcome["relic_ids"] = new[] { "DAUGHTER_OF_THE_WIND", "MR_STRUGGLES", "BING_BONG" };
                knownOutcome["has_randomness"] = true;
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".INITIAL.OPTIONS.TAKE_SOME_TIME"))
            {
                knownOutcome["hp_delta"] = -5;
                knownOutcome["has_unknown_effects"] = true;
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".INITIAL.OPTIONS.EXAMINE"))
            {
                knownOutcome["hp_delta"] = -15;
                knownOutcome["has_unknown_effects"] = true;
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }
        }

        return false;
    }
}
