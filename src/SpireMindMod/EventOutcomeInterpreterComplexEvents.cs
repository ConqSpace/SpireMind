namespace SpireMindMod;

internal static class EventOutcomeInterpreterComplexEvents
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
        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "CRYSTALSPHERE", "CRYSTAL_SPHERE"))
        {
            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".UNCOVER_FUTURE"))
            {
                knownOutcome["gold_delta"] = -75;
                knownOutcome["has_randomness"] = true;
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "pay variable gold, then resolve a multi-step crystal sphere reward pool" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                runtimeWarnings.Add("state_dependent_cost");
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".PAYMENT_PLAN"))
            {
                knownOutcome["curse_card_ids"] = new[] { "DEBT" };
                knownOutcome["has_randomness"] = true;
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "add Debt, then resolve a larger multi-step crystal sphere reward pool" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }
        }

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "TRIAL"))
        {
            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".INITIAL.OPTIONS.ACCEPT"))
            {
                knownOutcome["has_randomness"] = true;
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "advance into one of several random trial branches" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                confidence = "low";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".INITIAL.OPTIONS.REJECT")
                || EventOutcomeInterpreter.ContainsToken(normalizedText, ".REJECT.OPTIONS.ACCEPT"))
            {
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "reject path moves to another trial decision page" };
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                confidence = "low";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".DOUBLE_DOWN"))
            {
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "opens an abandon-run style follow-up instead of a normal event reward" };
                EventOutcomeInterpreter.MarkMultiStep(runtimeWarnings);
                confidence = "low";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".MERCHANT.OPTIONS.GUILTY"))
            {
                knownOutcome["curse_card_ids"] = new[] { "REGRET" };
                knownOutcome["relic_ids"] = new[] { "RANDOM_RELIC", "RANDOM_RELIC" };
                knownOutcome["has_randomness"] = true;
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "add Regret and gain two random relics" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".MERCHANT.OPTIONS.INNOCENT"))
            {
                knownOutcome["curse_card_ids"] = new[] { "SHAME" };
                knownOutcome["upgrade_count"] = 2;
                knownOutcome["notes"] = new[] { "add Shame and upgrade two cards" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".NOBLE.OPTIONS.GUILTY"))
            {
                knownOutcome["notes"] = new[] { "heal 10 hp; exact hp delta depends on current missing hp" };
                runtimeWarnings.Add("state_dependent_outcome");
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".NOBLE.OPTIONS.INNOCENT"))
            {
                knownOutcome["curse_card_ids"] = new[] { "REGRET" };
                knownOutcome["gold_delta"] = 300;
                knownOutcome["notes"] = new[] { "gain 300 gold and add Regret" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".NONDESCRIPT.OPTIONS.GUILTY"))
            {
                knownOutcome["curse_card_ids"] = new[] { "DOUBT" };
                knownOutcome["card_reward_count"] = 2;
                knownOutcome["has_randomness"] = true;
                knownOutcome["notes"] = new[] { "add Doubt and receive two card reward offerings" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".NONDESCRIPT.OPTIONS.INNOCENT"))
            {
                knownOutcome["curse_card_ids"] = new[] { "DOUBT" };
                knownOutcome["transform_count"] = 2;
                knownOutcome["notes"] = new[] { "add Doubt and transform two cards" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }
        }

        if (EventOutcomeInterpreter.ContainsEventName(normalizedText, "WELCOMETOWONGOS", "WELCOME_TO_WONGOS"))
        {
            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".BARGAIN_BIN"))
            {
                knownOutcome["gold_delta"] = -100;
                knownOutcome["relic_ids"] = new[] { "COMMON_RANDOM_RELIC" };
                knownOutcome["has_randomness"] = true;
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "buy a random common relic" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "medium";
                knownLevel = "partial";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".FEATURED_ITEM"))
            {
                knownOutcome["gold_delta"] = -200;
                knownOutcome["relic_ids"] = new[] { "FEATURED_ITEM_RELIC" };
                knownOutcome["notes"] = new[] { "buy the displayed featured relic" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".MYSTERY_BOX"))
            {
                knownOutcome["gold_delta"] = -300;
                knownOutcome["relic_ids"] = new[] { "WONGOS_MYSTERY_TICKET" };
                knownOutcome["notes"] = new[] { "buy mystery ticket relic" };
                confidence = "high";
                knownLevel = "known";
                return true;
            }

            if (EventOutcomeInterpreter.ContainsToken(normalizedText, ".LEAVE"))
            {
                knownOutcome["has_randomness"] = true;
                knownOutcome["has_unknown_effects"] = true;
                knownOutcome["notes"] = new[] { "leave branch can downgrade a random upgraded card" };
                EventOutcomeInterpreter.MarkRandom(runtimeWarnings);
                confidence = "low";
                knownLevel = "partial";
                return true;
            }
        }

        return false;
    }
}
