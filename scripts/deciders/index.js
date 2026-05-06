"use strict";

const { CommandDecider } = require("./command_decider");
const { CodexAppServerDecider } = require("./codex_app_server_decider");
const { LocalHttpDecider } = require("./local_http_decider");

function createDecider(options) {
  if (options.decisionBackend === "command") {
    return new CommandDecider(options);
  }

  if (options.decisionBackend === "app-server") {
    return new CodexAppServerDecider(options);
  }

  if (options.decisionBackend === "local-http" || options.decisionBackend === "local_http") {
    return new LocalHttpDecider(options);
  }

  throw new Error(`지원하지 않는 decision backend입니다: ${options.decisionBackend}`);
}

module.exports = {
  createDecider
};
