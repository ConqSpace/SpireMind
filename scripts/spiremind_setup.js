#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const readline = require("readline/promises");
const { stdin: input, stdout: output } = require("process");

const DEFAULT_CONFIG_PATH = path.resolve(__dirname, "..", "config", "local_setup.local.json");

function parseArgs(argv) {
  const options = {
    help: false,
    selfTest: false,
    configPath: DEFAULT_CONFIG_PATH
  };

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--help" || token === "-h") {
      options.help = true;
    } else if (token === "--self-test") {
      options.selfTest = true;
    } else if (token === "--config" && index + 1 < argv.length) {
      options.configPath = path.resolve(argv[index + 1]);
      index += 1;
    } else if (token.startsWith("--config=")) {
      options.configPath = path.resolve(token.slice("--config=".length));
    }
  }

  return options;
}

function showHelp() {
  process.stdout.write(`SpireMind 초기 세팅 CLI

Usage:
  node scripts/spiremind_setup.js
  node scripts/spiremind_setup.js --config config/my_setup.local.json

이 명령은 개인 PC에만 해당하는 게임 설치 위치와 모드 폴더를
config/local_setup.local.json에 저장합니다.
`);
}

function readJsonFile(filePath) {
  if (!fs.existsSync(filePath)) {
    return null;
  }

  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function normalizePathText(value) {
  return String(value || "").trim().replace(/^"(.*)"$/, "$1");
}

function inferModsDir(installPath) {
  const trimmed = normalizePathText(installPath);
  return trimmed ? path.join(trimmed, "mods") : "";
}

function inferExePath(installPath) {
  const trimmed = normalizePathText(installPath);
  if (!trimmed) {
    return "";
  }

  const candidates = [
    path.join(trimmed, "SlayTheSpire2.exe"),
    path.join(trimmed, "Slay the Spire 2.exe"),
    path.join(trimmed, "STS2.exe")
  ];
  return candidates.find((candidate) => fs.existsSync(candidate)) || candidates[0];
}

function getNested(object, pathSegments, fallback = "") {
  let current = object;
  for (const segment of pathSegments) {
    if (!current || typeof current !== "object") {
      return fallback;
    }
    current = current[segment];
  }
  return current ?? fallback;
}

async function askText(rl, label, defaultValue) {
  const suffix = defaultValue ? ` [${defaultValue}]` : "";
  const answer = normalizePathText(await rl.question(`${label}${suffix}: `));
  return answer || defaultValue || "";
}

async function askLaunchMode(rl, defaultValue) {
  const normalizedDefault = defaultValue === "Exe" ? "Exe" : "Steam";
  while (true) {
    const answer = (await askText(rl, "실행 방식 Steam/Exe", normalizedDefault)).trim();
    if (answer.toLowerCase() === "steam") {
      return "Steam";
    }
    if (answer.toLowerCase() === "exe") {
      return "Exe";
    }
    process.stdout.write("Steam 또는 Exe 중 하나를 입력하세요.\n");
  }
}

function buildConfig(answers) {
  return {
    schema_version: "spiremind.local_setup.v1",
    sts2: {
      install_path: answers.installPath,
      exe_path: answers.exePath,
      mods_dir: answers.modsDir,
      launch_mode: answers.launchMode,
      pck_path: answers.pckPath
    },
    bridge: {
      host: answers.bridgeHost,
      port: answers.bridgePort
    },
    defaults: {
      seed: answers.seed,
      character_id: answers.characterId
    }
  };
}

async function runSetup(options) {
  const existing = readJsonFile(options.configPath) || {};
  const existingSts2 = existing.sts2 || {};
  const existingBridge = existing.bridge || {};
  const existingDefaults = existing.defaults || {};

  process.stdout.write("SpireMind 초기 세팅을 시작합니다.\n");
  process.stdout.write("로컬 PC마다 달라지는 값만 저장합니다.\n\n");

  const rl = readline.createInterface({ input, output });
  try {
    const defaultInstallPath = getNested(existing, ["sts2", "install_path"], "");
    const installPath = await askText(rl, "STS2 설치 폴더", defaultInstallPath);
    const exePath = await askText(rl, "STS2 실행 파일", existingSts2.exe_path || inferExePath(installPath));
    const modsDir = await askText(rl, "STS2 mods 폴더", existingSts2.mods_dir || inferModsDir(installPath));
    const launchMode = await askLaunchMode(rl, existingSts2.launch_mode || "Steam");
    const pckPath = await askText(rl, "선택 사항: SpireMind.pck 경로", existingSts2.pck_path || "");
    const bridgeHost = await askText(rl, "브리지 host", existingBridge.host || "127.0.0.1");
    const bridgePortText = await askText(rl, "브리지 port", String(existingBridge.port || 17832));
    const seed = await askText(rl, "기본 시드", existingDefaults.seed || "7MJCUHEB5Q");
    const characterId = await askText(rl, "기본 캐릭터", existingDefaults.character_id || "Ironclad");

    const bridgePort = Number.parseInt(bridgePortText, 10);
    if (!Number.isFinite(bridgePort) || bridgePort <= 0) {
      throw new Error("브리지 port는 양의 정수여야 합니다.");
    }

    const config = buildConfig({
      installPath,
      exePath,
      modsDir,
      launchMode,
      pckPath,
      bridgeHost,
      bridgePort,
      seed,
      characterId
    });

    fs.mkdirSync(path.dirname(options.configPath), { recursive: true });
    fs.writeFileSync(options.configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");

    process.stdout.write(`\n저장 완료: ${options.configPath}\n`);
    process.stdout.write("다음 명령으로 설정을 사용하는지 확인할 수 있습니다.\n");
    process.stdout.write("  .\\scripts\\runtime_smoke_check.ps1 -Help\n");
    process.stdout.write("  .\\scripts\\runtime_smoke_check.ps1 -Build -Deploy -LaunchGame -WhatIf\n");
  } finally {
    rl.close();
  }
}

function runSelfTest() {
  const config = buildConfig({
    installPath: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2",
    exePath: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\SlayTheSpire2.exe",
    modsDir: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\mods",
    launchMode: "Steam",
    pckPath: "",
    bridgeHost: "127.0.0.1",
    bridgePort: 17832,
    seed: "7MJCUHEB5Q",
    characterId: "Ironclad"
  });
  if (config.schema_version !== "spiremind.local_setup.v1" || config.sts2.mods_dir === "") {
    throw new Error("setup config self-test failed");
  }
  process.stdout.write(`${JSON.stringify({ status: "self_test_passed" }, null, 2)}\n`);
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (options.help) {
    showHelp();
    return;
  }
  if (options.selfTest) {
    runSelfTest();
    return;
  }
  await runSetup(options);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.stack || error.message : String(error)}\n`);
  process.exitCode = 1;
});
