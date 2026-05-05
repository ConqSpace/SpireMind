"use strict";

const { CommandDecider } = require("./command_decider");
const { CodexAppServerDecider } = require("./codex_app_server_decider");

function createDecider(options) {
  if (options.decisionBackend === "command") {
    return new CommandDecider(options);
  }

  if (options.decisionBackend === "app-server") {
    return new CodexAppServerDecider(options);
  }

  throw new Error(`지원하지 않는 decision backend입니다: ${options.decisionBackend}`);
}

module.exports = {
  createDecider
};
