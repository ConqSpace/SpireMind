#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const readline = require("readline/promises");
const { stdin: input, stdout: output } = require("process");

const REPO_ROOT = path.resolve(__dirname, "..");
const DEFAULT_CONFIG_PATH = path.join(REPO_ROOT, "config", "local_setup.local.json");
const DEFAULT_LOCAL_PROPS_PATH = path.join(REPO_ROOT, "src", "SpireMindMod", "SpireMind.Local.props");

function parseArgs(argv) {
  const options = {
    help: false,
    check: false,
    selfTest: false,
    configPath: DEFAULT_CONFIG_PATH,
    localPropsPath: DEFAULT_LOCAL_PROPS_PATH
  };

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (token === "--help" || token === "-h") {
      options.help = true;
    } else if (token === "--check") {
      options.check = true;
    } else if (token === "--self-test") {
      options.selfTest = true;
    } else if (token === "--config" && index + 1 < argv.length) {
      options.configPath = path.resolve(argv[index + 1]);
      index += 1;
    } else if (token.startsWith("--config=")) {
      options.configPath = path.resolve(token.slice("--config=".length));
    } else if (token === "--local-props" && index + 1 < argv.length) {
      options.localPropsPath = path.resolve(argv[index + 1]);
      index += 1;
    } else if (token.startsWith("--local-props=")) {
      options.localPropsPath = path.resolve(token.slice("--local-props=".length));
    }
  }

  return options;
}

function showHelp() {
  process.stdout.write(`SpireMind 초기 세팅 CLI

사용법:
  node scripts/spiremind_setup.js
  node scripts/spiremind_setup.js --check
  node scripts/spiremind_setup.js --config config/my_setup.local.json

역할:
  - config/local_setup.local.json 생성
  - src/SpireMindMod/SpireMind.Local.props 생성
  - STS2 실행 파일, mods 폴더, 빌드 참조 경로 점검

개인 PC 경로를 담는 *.local.json과 SpireMind.Local.props는 Git에 올리지 않습니다.
`);
}

function normalizePathText(value) {
  return String(value || "").trim().replace(/^"(.*)"$/, "$1");
}

function readJsonFile(filePath) {
  if (!fs.existsSync(filePath)) {
    return null;
  }

  return JSON.parse(fs.readFileSync(filePath, "utf8"));
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

function firstExistingPath(candidates) {
  return candidates.find((candidate) => candidate && fs.existsSync(candidate)) || candidates[0] || "";
}

function inferExePath(installPath) {
  const trimmed = normalizePathText(installPath);
  if (!trimmed) {
    return "";
  }

  return firstExistingPath([
    path.join(trimmed, "SlayTheSpire2.exe"),
    path.join(trimmed, "Slay the Spire 2.exe"),
    path.join(trimmed, "STS2.exe")
  ]);
}

function inferModsDir(installPath) {
  const trimmed = normalizePathText(installPath);
  return trimmed ? path.join(trimmed, "mods") : "";
}

function inferAssemblyPath(installPath) {
  const trimmed = normalizePathText(installPath);
  if (!trimmed) {
    return "";
  }

  return firstExistingPath([
    path.join(trimmed, "SlayTheSpire2_Data", "Managed", "sts2.dll"),
    path.join(trimmed, "Slay the Spire 2_Data", "Managed", "sts2.dll"),
    path.join(trimmed, "sts2.dll")
  ]);
}

function inferGameDataPath(installPath) {
  const trimmed = normalizePathText(installPath);
  if (!trimmed) {
    return "";
  }

  return firstExistingPath([
    path.join(trimmed, "data_sts2_windows_x86_64"),
    path.join(trimmed, "data_sts2_windows"),
    path.join(trimmed, "data")
  ]);
}

function xmlEscape(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&apos;");
}

function buildConfig(answers) {
  return {
    schema_version: "spiremind.local_setup.v1",
    sts2: {
      install_path: answers.installPath,
      exe_path: answers.exePath,
      mods_dir: answers.modsDir,
      launch_mode: answers.launchMode,
      pck_path: answers.pckPath,
      assembly_path: answers.assemblyPath,
      game_data_path: answers.gameDataPath
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

function buildLocalProps(config) {
  const sts2 = config.sts2 || {};
  return `<Project>
  <PropertyGroup>
    <Sts2AssemblyPath>${xmlEscape(sts2.assembly_path)}</Sts2AssemblyPath>
    <Sts2GameDataPath>${xmlEscape(sts2.game_data_path)}</Sts2GameDataPath>
    <Sts2ModsDir>${xmlEscape(sts2.mods_dir)}</Sts2ModsDir>
  </PropertyGroup>
</Project>
`;
}

function writeLocalSetup(configPath, localPropsPath, config) {
  fs.mkdirSync(path.dirname(configPath), { recursive: true });
  fs.writeFileSync(configPath, `${JSON.stringify(config, null, 2)}\n`, "utf8");

  fs.mkdirSync(path.dirname(localPropsPath), { recursive: true });
  fs.writeFileSync(localPropsPath, buildLocalProps(config), "utf8");
}

function makeCheck(name, status, detail, hint = "") {
  return { name, status, detail, hint };
}

function checkPathExists(name, filePath, missingStatus, hint) {
  if (!filePath) {
    return makeCheck(name, missingStatus, "경로가 비어 있습니다.", hint);
  }
  if (fs.existsSync(filePath)) {
    return makeCheck(name, "PASS", filePath);
  }
  return makeCheck(name, missingStatus, `찾을 수 없습니다: ${filePath}`, hint);
}

function checkSetup(options) {
  const checks = [];
  const config = readJsonFile(options.configPath);

  if (!config) {
    checks.push(makeCheck(
      "local_setup.local.json",
      "FAIL",
      `설정 파일이 없습니다: ${options.configPath}`,
      "node .\\scripts\\spiremind_setup.js를 먼저 실행하세요."));
    return checks;
  }

  checks.push(makeCheck("local_setup.local.json", "PASS", options.configPath));
  const sts2 = config.sts2 || {};
  checks.push(checkPathExists("STS2 설치 폴더", sts2.install_path, "FAIL", "Steam 라이브러리의 Slay the Spire 2 설치 폴더를 입력하세요."));
  checks.push(checkPathExists("STS2 실행 파일", sts2.exe_path, "WARN", "Steam 실행 방식을 쓰면 없어도 시작할 수 있지만, 직접 실행 검증에는 필요합니다."));
  checks.push(checkPathExists("STS2 mods 폴더", sts2.mods_dir, "FAIL", "모드 로더가 만든 mods 폴더를 확인하세요."));
  checks.push(checkPathExists("STS2 assembly", sts2.assembly_path, "FAIL", "C# 빌드가 sts2.dll을 참조하려면 이 경로가 필요합니다."));
  checks.push(checkPathExists("STS2 game data", sts2.game_data_path, "FAIL", "GodotSharp.dll을 찾기 위한 게임 데이터 폴더입니다."));
  checks.push(checkPathExists("SpireMind.Local.props", options.localPropsPath, "FAIL", "초기 세팅 CLI가 이 파일을 생성해야 합니다."));

  const bridgePort = Number.parseInt(String((config.bridge || {}).port), 10);
  if (Number.isFinite(bridgePort) && bridgePort > 0) {
    checks.push(makeCheck("브리지 포트", "PASS", String(bridgePort)));
  } else {
    checks.push(makeCheck("브리지 포트", "FAIL", "양의 정수가 아닙니다.", "초기 세팅 CLI를 다시 실행하세요."));
  }

  return checks;
}

function printChecks(checks) {
  let failCount = 0;
  let warnCount = 0;
  for (const check of checks) {
    if (check.status === "FAIL") {
      failCount += 1;
    } else if (check.status === "WARN") {
      warnCount += 1;
    }

    process.stdout.write(`[${check.status}] ${check.name}: ${check.detail}\n`);
    if (check.hint) {
      process.stdout.write(`  다음 행동: ${check.hint}\n`);
    }
  }

  const overall = failCount > 0 ? "FAIL" : warnCount > 0 ? "WARN" : "PASS";
  process.stdout.write(`\n전체 결과: ${overall}\n`);
  return overall;
}

async function askText(rl, label, defaultValue) {
  const suffix = defaultValue ? ` [${defaultValue}]` : "";
  const answer = normalizePathText(await rl.question(`${label}${suffix}: `));
  return answer || defaultValue || "";
}

async function askLaunchMode(rl, defaultValue) {
  const normalizedDefault = defaultValue === "Exe" ? "Exe" : "Steam";
  while (true) {
    const answer = (await askText(rl, "실행 방식 Steam/Exe", normalizedDefault)).trim().toLowerCase();
    if (answer === "steam") {
      return "Steam";
    }
    if (answer === "exe") {
      return "Exe";
    }
    process.stdout.write("Steam 또는 Exe 중 하나를 입력하세요.\n");
  }
}

async function runSetup(options) {
  const existing = readJsonFile(options.configPath) || {};
  const existingSts2 = existing.sts2 || {};
  const existingBridge = existing.bridge || {};
  const existingDefaults = existing.defaults || {};

  process.stdout.write("SpireMind 초기 세팅을 시작합니다.\n");
  process.stdout.write("게임 실행, 모드 배포, C# 빌드에 필요한 로컬 경로를 저장합니다.\n\n");

  const rl = readline.createInterface({ input, output });
  try {
    const installPath = await askText(rl, "STS2 설치 폴더", existingSts2.install_path || "");
    const exePath = await askText(rl, "STS2 실행 파일", existingSts2.exe_path || inferExePath(installPath));
    const modsDir = await askText(rl, "STS2 mods 폴더", existingSts2.mods_dir || inferModsDir(installPath));
    const assemblyPath = await askText(rl, "STS2 assembly sts2.dll", existingSts2.assembly_path || inferAssemblyPath(installPath));
    const gameDataPath = await askText(rl, "STS2 game data 폴더", existingSts2.game_data_path || inferGameDataPath(installPath));
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
      assemblyPath,
      gameDataPath,
      launchMode,
      pckPath,
      bridgeHost,
      bridgePort,
      seed,
      characterId
    });

    writeLocalSetup(options.configPath, options.localPropsPath, config);

    process.stdout.write(`\n저장 완료: ${options.configPath}\n`);
    process.stdout.write(`빌드 설정 생성 완료: ${options.localPropsPath}\n\n`);
    const overall = printChecks(checkSetup(options));
    if (overall === "PASS") {
      process.stdout.write("\n다음 단계: .\\scripts\\build_mod.ps1\n");
    } else {
      process.stdout.write("\n위 경고와 실패 항목을 먼저 확인한 뒤 빌드하세요.\n");
    }
  } finally {
    rl.close();
  }
}

function runSelfTest() {
  const config = buildConfig({
    installPath: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2",
    exePath: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\SlayTheSpire2.exe",
    modsDir: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\mods",
    assemblyPath: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\SlayTheSpire2_Data\\Managed\\sts2.dll",
    gameDataPath: "D:\\SteamLibrary\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64",
    launchMode: "Steam",
    pckPath: "",
    bridgeHost: "127.0.0.1",
    bridgePort: 17832,
    seed: "7MJCUHEB5Q",
    characterId: "Ironclad"
  });
  const localProps = buildLocalProps(config);
  if (config.schema_version !== "spiremind.local_setup.v1"
    || config.sts2.mods_dir === ""
    || !localProps.includes("<Sts2AssemblyPath>")
    || !localProps.includes("<Sts2GameDataPath>")
    || !localProps.includes("<Sts2ModsDir>")) {
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
  if (options.check) {
    const overall = printChecks(checkSetup(options));
    process.exitCode = overall === "FAIL" ? 1 : 0;
    return;
  }
  await runSetup(options);
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.stack || error.message : String(error)}\n`);
  process.exitCode = 1;
});
