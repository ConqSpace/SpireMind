"use strict";

function decisionOutputSchema() {
  return {
    type: "object",
    properties: {
      selected_action_id: { type: "string" },
      reason: { type: "string" }
    },
    required: ["selected_action_id", "reason"],
    additionalProperties: false
  };
}

function postRunReportOutputSchema() {
  return {
    type: "object",
    properties: {
      death_cause: { type: "string" },
      what_i_did_well: {
        type: "array",
        items: { type: "string" }
      },
      what_i_did_poorly: {
        type: "array",
        items: { type: "string" }
      },
      key_mistakes: {
        type: "array",
        items: { type: "string" }
      },
      next_run_adjustments: {
        type: "array",
        items: { type: "string" }
      },
      adapter_observations: {
        type: "array",
        items: { type: "string" }
      }
    },
    required: [
      "death_cause",
      "what_i_did_well",
      "what_i_did_poorly",
      "key_mistakes",
      "next_run_adjustments",
      "adapter_observations"
    ],
    additionalProperties: false
  };
}

module.exports = {
  decisionOutputSchema,
  postRunReportOutputSchema
};
