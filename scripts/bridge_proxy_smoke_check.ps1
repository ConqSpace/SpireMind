param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
    [string]$BridgeUrl = "http://127.0.0.1:17832",
    [string]$CombatStatePath = (Join-Path $env:APPDATA "SlayTheSpire2\SpireMind\combat_state.json"),
    [switch]$UseExistingBridge,
    [switch]$KeepBridgeRunning
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$OutputEncoding = [System.Text.UTF8Encoding]::new()

function Test-BridgeHealth {
    param([string]$Url)

    try {
        return Invoke-RestMethod -Method Get -Uri "$Url/health" -TimeoutSec 2
    } catch {
        return $null
    }
}

function Wait-BridgeHealth {
    param(
        [string]$Url,
        [int]$Attempts = 40
    )

    for ($index = 0; $index -lt $Attempts; $index += 1) {
        $health = Test-BridgeHealth -Url $Url
        if ($null -ne $health -and $health.ok -eq $true) {
            return $health
        }

        Start-Sleep -Milliseconds 250
    }

    throw "브리지 건강 확인에 실패했습니다: $Url"
}

function Start-BridgeForSmoke {
    param([string]$Root)

    $bridgePath = Join-Path $Root "bridge\spiremind_bridge.js"
    if (-not (Test-Path $bridgePath)) {
        throw "브리지 파일을 찾지 못했습니다: $bridgePath"
    }

    return Start-Process -FilePath "node" -ArgumentList @($bridgePath) -WorkingDirectory $Root -WindowStyle Hidden -PassThru
}

function Invoke-McpProxySmoke {
    param(
        [string]$Root,
        [string]$Url
    )

    $nodeScript = @'
const { spawn } = require("child_process");

const projectRoot = process.argv[2];
const bridgeUrl = process.argv[3];
const child = spawn(process.execPath, ["bridge/spiremind_mcp_proxy.js", "--bridge-url", bridgeUrl], {
  cwd: projectRoot,
  stdio: ["pipe", "pipe", "pipe"]
});

let buffer = Buffer.alloc(0);
let nextId = 1;
const pending = new Map();

const timeout = setTimeout(() => {
  child.kill();
  console.error("MCP_PROXY_TIMEOUT");
  process.exit(1);
}, 10000);

child.stderr.setEncoding("utf8");
child.stderr.on("data", (chunk) => process.stderr.write(chunk));

function consumeMessages() {
  while (true) {
    const headerEnd = buffer.indexOf(Buffer.from("\r\n\r\n"));
    if (headerEnd < 0) {
      return;
    }

    const header = buffer.slice(0, headerEnd).toString("utf8");
    const match = header.match(/Content-Length:\s*(\d+)/i);
    if (!match) {
      throw new Error("Content-Length 헤더를 해석하지 못했습니다.");
    }

    const length = Number(match[1]);
    const bodyStart = headerEnd + 4;
    if (buffer.length < bodyStart + length) {
      return;
    }

    const body = buffer.slice(bodyStart, bodyStart + length).toString("utf8");
    buffer = buffer.slice(bodyStart + length);
    handleMessage(JSON.parse(body));
  }
}

function handleMessage(message) {
  if (Object.prototype.hasOwnProperty.call(message, "id") && pending.has(message.id)) {
    pending.get(message.id)(message);
    pending.delete(message.id);
  }
}

child.stdout.on("data", (chunk) => {
  buffer = Buffer.concat([buffer, chunk]);
  consumeMessages();
});

function request(method, params) {
  const id = nextId++;
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  return new Promise((resolve) => pending.set(id, resolve));
}

(async () => {
  const init = await request("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "bridge-proxy-smoke", version: "1.0.0" }
  });

  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized", params: {} }) + "\n");

  const stateResponse = await request("tools/call", { name: "get_current_state", arguments: {} });
  const state = JSON.parse(stateResponse.result.content[0].text);
  const actionId = state.legal_action_ids.includes("end_turn") ? "end_turn" : state.legal_action_ids[0];
  if (!actionId) {
    throw new Error("제출할 legal_action_id가 없습니다.");
  }

  const submitResponse = await request("tools/call", {
    name: "submit_action",
    arguments: {
      selected_action_id: actionId,
      source: "bridge-proxy-smoke",
      expected_state_version: state.state_version
    }
  });
  const submitPayload = JSON.parse(submitResponse.result.content[0].text);

  clearTimeout(timeout);
  child.kill();
  console.log(JSON.stringify({
    server: init.result.serverInfo.name,
    state_id: state.state_id,
    state_version: state.state_version,
    selected_action_id: submitPayload.latest_action.selected_action_id,
    valid: submitPayload.latest_action.valid
  }));
})().catch((error) => {
  clearTimeout(timeout);
  child.kill();
  console.error(error.stack || String(error));
  process.exit(1);
});
'@

    $resultText = $nodeScript | node - $Root $Url
    if ($LASTEXITCODE -ne 0) {
        throw "MCP 프록시 스모크 테스트가 실패했습니다."
    }

    return $resultText | ConvertFrom-Json
}

if (-not (Test-Path $CombatStatePath)) {
    throw "전투 상태 파일을 찾지 못했습니다: $CombatStatePath"
}

$startedBridge = $null
$existingHealth = Test-BridgeHealth -Url $BridgeUrl

if ($null -eq $existingHealth) {
    if ($UseExistingBridge) {
        throw "기존 브리지가 실행 중이 아닙니다: $BridgeUrl"
    }

    Write-Host "[SpireMind] Starting bridge server."
    $startedBridge = Start-BridgeForSmoke -Root $ProjectRoot
    $null = Wait-BridgeHealth -Url $BridgeUrl
} else {
    Write-Host "[SpireMind] Using running bridge server."
}

try {
    $combatStateJson = Get-Content -Path $CombatStatePath -Raw -Encoding UTF8
    $null = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/state" -ContentType "application/json; charset=utf-8" -Body $combatStateJson -TimeoutSec 5

    $current = Invoke-RestMethod -Method Get -Uri "$BridgeUrl/state/current" -TimeoutSec 5
    if ($current.status -ne "ready") {
        throw "브리지 상태가 ready가 아닙니다: $($current.status)"
    }

    $selectedActionId = "end_turn"
    if (-not ($current.legal_action_ids -contains $selectedActionId)) {
        $selectedActionId = @($current.legal_action_ids)[0]
    }

    if ([string]::IsNullOrWhiteSpace($selectedActionId)) {
        throw "제출할 legal_action_id가 없습니다."
    }

    $submitBody = [ordered]@{
        selected_action_id = $selectedActionId
        source = "http-smoke"
        expected_state_version = [int]$current.state_version
    } | ConvertTo-Json -Compress

    $httpSubmit = Invoke-RestMethod -Method Post -Uri "$BridgeUrl/action/submit" -ContentType "application/json; charset=utf-8" -Body $submitBody -TimeoutSec 5
    if ($httpSubmit.latest_action.valid -ne $true) {
        throw "HTTP 행동 제출이 유효하지 않습니다."
    }

    $mcpSubmit = Invoke-McpProxySmoke -Root $ProjectRoot -Url $BridgeUrl
    if ($mcpSubmit.valid -ne $true) {
        throw "MCP 프록시 행동 제출이 유효하지 않습니다."
    }

    [pscustomobject]@{
        status = "PASS"
        bridge_url = $BridgeUrl
        state_id = $current.state_id
        state_version = $current.state_version
        http_selected_action_id = $httpSubmit.latest_action.selected_action_id
        mcp_selected_action_id = $mcpSubmit.selected_action_id
    } | ConvertTo-Json -Depth 8
} finally {
    if ($null -ne $startedBridge -and -not $KeepBridgeRunning -and -not $startedBridge.HasExited) {
        Stop-Process -Id $startedBridge.Id -Force
    }
}
