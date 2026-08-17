/*
FILE PURPOSE

This dependency-free client discovers and polls the laptop's current read-only public tunnel,
validates its telemetry envelope, and renders confirmed NGU state into the static dashboard. Local
and private-host copies use their own origin. The discovery record contains only the tunnel URL;
this client sends no commands, persists no game data, and exposes no mutation endpoint.
*/
(() => {
  "use strict";

  const publicDashboardHosts = new Set(["jehlp.net", "www.jehlp.net", "jwknt.github.io"]);
  const publicFeed = publicDashboardHosts.has(window.location.hostname);
  const endpointDiscoveryUrl = "https://api.github.com/gists/574be4aaf834537b70c62e4505f5ea31";
  const endpointDiscoveryFile = "ngu-dashboard-endpoint.json";
  let endpoint = publicFeed ? "" : "/api/state";
  let nextEndpointDiscovery = 0;
  const pollMs = 1000;
  let lastSequence = -1;

  const byId = (id) => document.getElementById(id);
  const number = (value, fallback = 0) => Number.isFinite(Number(value)) ? Number(value) : fallback;
  const text = (value, fallback = "—") => value === null || value === undefined || value === "" ? fallback : String(value);
  const setText = (id, value, fallback) => { const node = byId(id); if (node) node.textContent = text(value, fallback); };

  function compactDecimal(value, places) {
    return value.toFixed(places).replace(/(\.\d*?[1-9])0+$|\.0+$/, "$1");
  }

  function shortNumber(value) {
    const n = number(value);
    const abs = Math.abs(n);
    if (abs >= 1e15) return `${compactDecimal(n / 1e15, abs >= 1e17 ? 0 : 2)}Q`;
    if (abs >= 1e12) return `${compactDecimal(n / 1e12, abs >= 1e14 ? 0 : 2)}T`;
    if (abs >= 1e9) return `${compactDecimal(n / 1e9, abs >= 1e11 ? 0 : 2)}B`;
    if (abs >= 1e6) return `${compactDecimal(n / 1e6, abs >= 1e8 ? 0 : 2)}M`;
    if (abs >= 1e3) return `${compactDecimal(n / 1e3, abs >= 1e5 ? 0 : 1)}K`;
    return Math.round(n).toLocaleString();
  }

  function duration(seconds, approximate = false) {
    const total = number(seconds, -1);
    if (total < 0) return "No finite ETA";
    if (total < 60) return `${approximate ? "about " : ""}${Math.max(0, Math.round(total))}s`;
    const days = Math.floor(total / 86400);
    const hours = Math.floor(total % 86400 / 3600);
    const minutes = Math.floor(total % 3600 / 60);
    const secs = Math.floor(total % 60);
    const parts = [];
    if (days) parts.push(`${days}d`);
    if (hours) parts.push(`${hours}h`);
    if (minutes && parts.length < 2) parts.push(`${minutes}m`);
    if (!days && !hours && secs && parts.length < 2) parts.push(`${secs}s`);
    return `${approximate ? "about " : ""}${parts.join(" ")}`;
  }

  function scientific(value) {
    const n = number(value);
    if (!n) return "0";
    return n.toExponential(3).replace("e+", "e");
  }

  function percent(numerator, denominator) {
    const d = number(denominator);
    return d > 0 ? Math.max(0, Math.min(100, number(numerator) / d * 100)) : 0;
  }

  function sentence(value) {
    const result = text(value, "");
    if (!result) return "—";
    return /[.!?]$/.test(result) ? result : `${result}.`;
  }

  async function discoverEndpoint() {
    if (!publicFeed) return endpoint;
    if (endpoint) return endpoint;
    if (Date.now() < nextEndpointDiscovery) return "";
    nextEndpointDiscovery = Date.now() + 30000;
    const response = await fetch(endpointDiscoveryUrl, {
      cache: "no-store",
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!response.ok) throw new Error(`endpoint discovery returned ${response.status}`);
    const gist = await response.json();
    const content = gist?.files?.[endpointDiscoveryFile]?.content;
    const apiBase = JSON.parse(content || "{}").apiBase || "";
    if (!/^https:\/\/[a-z0-9-]+\.trycloudflare\.com$/.test(apiBase)) {
      throw new Error("endpoint discovery returned an invalid tunnel URL");
    }
    endpoint = `${apiBase}/api/state`;
    return endpoint;
  }

  function setConnection(state, detail) {
    const node = byId("connection-state");
    node.dataset.state = state;
    node.textContent = state === "live" ? `● ${publicFeed ? "Laptop feed" : "Local client"} live`
      : state === "stale" ? `● ${publicFeed ? "Laptop feed" : "Local client"} stale`
      : state === "connecting" ? `○ Connecting ${publicFeed ? "to laptop" : "locally"}…`
        : `○ ${publicFeed ? "Laptop feed" : "Local client"} offline`;
    setText("connection-detail", detail);
  }

  function resourceEta(state, prefix) {
    const target = purchaseName(state, prefix);
    const shortfall = number(state[`${prefix}Shortfall`]);
    const eta = number(state[`${prefix}EtaSeconds`], -1);
    if (target && shortfall > 0) return `${shortNumber(shortfall)} until ${target}${eta >= 0 ? ` · ${duration(eta, true)}` : ""}`;
    if (target) return `Ready for ${target}`;
    return eta >= 0 ? duration(eta, true) : "Continuously re-priced";
  }

  function purchaseName(state, prefix) {
    const explicit = text(state[`${prefix}TargetName`], "");
    if (explicit) return explicit;
    const decision = text(state[`${prefix}Decision`], "");
    const pattern = prefix === "ap"
      ? /(?:saving|holding) AP for (.+?)(?:\s*\(|;|$)/i
      : /(?:saving|holding)(?: briefly)? for (.+?)(?:\s*\(|:|;|$)/i;
    const match = decision.match(pattern);
    return match ? match[1].trim() : "next validated purchase";
  }

  function renderHeadline(s) {
    const hold = Boolean(s.rebirthExecutionHold);
    const rebirthRemaining = Math.max(0, number(s.rebirthSeconds) - number(s.rebirthElapsed));
    setText("metric-rebirth", hold ? "Unscheduled hold" : duration(rebirthRemaining));
    setText("metric-rebirth-note", hold ? "waiting for a strict Number improvement" : `target run age ${duration(s.rebirthSeconds)}`);

    const boss = number(s.bossRecordTargetId || s.nextBoss);
    setText("metric-boss-label", `Boss #${boss}`);
    setText("metric-boss", duration(s.bossDefeatEtaSeconds, true));
    setText("metric-boss-note", text(s.bossViabilityReason, "combat model pending"));

    setText("metric-adventure", s.adventureTargetName, "Selecting route");
    const routeMode = s.majorUnlockActive ? `major unlock · ${s.majorUnlockName}`
      : s.collectionIsBackfill ? "MAXX backfill" : s.adventureBossOnlyForSet ? "boss-only collection" : "forward progression";
    setText("metric-adventure-note", routeMode);

    const expName = purchaseName(s, "exp");
    const expShortfall = number(s.expShortfall);
    setText("metric-exp", expShortfall > 0 ? `${shortNumber(expShortfall)} XP` : "Ready");
    setText("metric-exp-note", `${expShortfall > 0 ? "until" : "for"} ${expName}`);
  }

  function renderRoute(s) {
    setText("objective", s.objective, "Re-evaluating progression route.");
    setText("run-stage", `${text(s.stage, "unknown stage")} · ${text(s.syncState, "unsynchronized")}`);
    setText("route-title", s.loadoutObjective || s.objective, "Waiting for a synchronized transaction");
    setText("route-reason", sentence(s.adventureControlReason || s.loadoutDecision || s.objective));
    setText("fact-mode", `${text(s.mode, "unknown")} · ${s.mutationsEnabled ? "automation active" : "read only"}`);
    setText("fact-snapshot", `#${number(s.decisionSequence).toLocaleString()}`);
    setText("fact-run-age", duration(s.rebirthElapsed));
    setText("fact-build", text(s.buildId, "—").slice(0, 8));
  }

  function renderResources(s) {
    const energyAllocated = number(s.energyAllocated);
    const energyCurrent = number(s.energyCurrent);
    const magicAllocated = number(s.magicAllocated);
    const magicCurrent = number(s.magicCurrent);
    setText("resource-summary", `${shortNumber(energyAllocated + magicAllocated)} assigned · ${shortNumber(number(s.energyIdle) + number(s.magicIdle))} idle`);

    setText("energy-value", `${shortNumber(energyAllocated)} / ${shortNumber(energyCurrent)}`);
    setText("energy-rate", `+${shortNumber(s.energyIncomePerSecond)}/s`);
    setText("energy-decision", sentence(s.energyIdleReason));
    byId("energy-meter").style.width = `${percent(energyAllocated, energyCurrent)}%`;
    setText("energy-bt", shortNumber(s.energyBasicTrainingAllocated));
    setText("energy-other", shortNumber(s.energyNonBasicTrainingAllocated));
    setText("energy-idle", shortNumber(s.energyIdle));

    setText("magic-value", `${shortNumber(magicAllocated)} / ${shortNumber(magicCurrent)}`);
    setText("magic-rate", `+${shortNumber(s.magicIncomePerSecond)}/s`);
    setText("magic-decision", sentence(s.magicAllocationDecision));
    byId("magic-meter").style.width = `${percent(magicAllocated, magicCurrent)}%`;
    setText("magic-blood", shortNumber(s.magicBloodAllocated));
    setText("magic-tm", shortNumber(s.magicTimeMachineAllocated));
    setText("magic-idle", shortNumber(s.magicIdle));

    setText("exp-balance", `${shortNumber(s.exp)} XP`);
    setText("exp-decision", sentence(s.expDecision));
    setText("exp-eta", resourceEta(s, "exp"));
    setText("ap-balance", `${shortNumber(s.ap)} AP`);
    setText("ap-decision", sentence(s.apDecision));
    setText("ap-eta", `${shortNumber(s.apShortfall)} until ${purchaseName(s, "ap")}${number(s.apEtaSeconds, -1) >= 0 ? ` · ${duration(s.apEtaSeconds, true)}` : ""}`);
    setText("gold-balance", `${shortNumber(s.gold)} Gold`);
    setText("gold-decision", sentence(s.goldDecision));
    setText("gold-rate", `${shortNumber(s.goldIncomePerSecond)} net Gold/s`);
  }

  function renderCombatInventory(s) {
    setText("combat-boss", `Boss #${number(s.bossSelectedId)}${s.bossTargetMatchesSelected ? " · record target" : ` · catching up to #${number(s.bossRecordTargetId)}`}`);
    setText("combat-eta", `${duration(s.bossDefeatEtaSeconds, true)} · ${text(s.bossEtaConfidence, "model pending")}`);
    setText("combat-zone", `${text(s.adventureTargetName, "—")} · ${text(s.adventureControlReason, "route pending")}`);
    setText("combat-stats", `${shortNumber(s.adventurePower)} / ${shortNumber(s.adventureToughness)}`);
    setText("combat-hp", `${shortNumber(s.adventureHP)} / ${shortNumber(s.adventureMaxHP)}`);
    setText("combat-collection", s.collectionMissingSummary || s.collectionReason, "—");
    setText("combat-reason", sentence(s.bossViabilityReason));

    setText("gear-objective", s.loadoutObjective, "—");
    setText("inventory-space", `${number(s.inventoryUsedSlots)}/${number(s.inventoryTotalSlots)} used · ${number(s.inventoryFreeSlots)} free`);
    setText("inventory-pressure", text(s.inventoryPressure, "unknown"));
    setText("boost-target", sentence(s.boostDecision));
    setText("trash-policy", sentence(s.trashDecision));
    setText("gear-decision", sentence(s.loadoutDecision));
  }

  function renderRebirth(s) {
    const hold = Boolean(s.rebirthExecutionHold);
    setText("rebirth-state", hold ? "unscheduled safety hold" : `${duration(Math.max(0, number(s.rebirthSeconds) - number(s.rebirthElapsed)))} remaining`);
    setText("rebirth-reason", sentence(s.rebirthReason));
    setText("rebirth-current", scientific(s.rebirthCurrentAttackMultiplier));
    setText("rebirth-preview", scientific(s.rebirthNextAttackMultiplierPreview));
    setText("rebirth-ratio", `${number(s.rebirthProjectedAttackMultiplier).toFixed(4)}×`);
    setText("rebirth-candidates", `${number(s.rebirthCandidateCount).toLocaleString()} · ${text(s.rebirthOptimizerModel, "model pending")}`);
    setText("rebirth-safety", sentence(s.rebirthSafetyBlockReason));
  }

  function renderTraining(s) {
    setText("training-policy", s.trainingGoal || s.basicTrainingLongHorizonPolicy, "—");
    const body = byId("training-body");
    const rows = Array.isArray(s.energyAllocationBreakdown) ? s.energyAllocationBreakdown : [];
    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="5">No Basic Training allocation is active.</td></tr>';
      return;
    }
    body.replaceChildren(...rows.map((row) => {
      const tr = document.createElement("tr");
      const values = [
        row.pair,
        `${shortNumber(row.attackEnergy)} / cap ${shortNumber(row.attackCap)}`,
        `${shortNumber(row.defenseEnergy)} / cap ${shortNumber(row.defenseCap)}`,
        shortNumber(row.totalEnergy),
        `${number(row.attackLevelsPerSecond).toFixed(1)} / ${number(row.defenseLevelsPerSecond).toFixed(1)} lv/s`,
      ];
      for (const value of values) { const td = document.createElement("td"); td.textContent = value; tr.append(td); }
      return tr;
    }));
  }

  function renderEvents(events) {
    const list = byId("event-list");
    if (!Array.isArray(events) || !events.length) {
      list.innerHTML = "<li>No key events have been recorded by the live feed yet.</li>";
      return;
    }
    list.replaceChildren(...events.slice(0, 30).map((event) => {
      const item = document.createElement("li");
      const timeNode = document.createElement("span");
      timeNode.className = "event-time";
      timeNode.textContent = text(event.clock || event.time, "—");
      const kind = document.createElement("span");
      kind.className = "event-kind";
      kind.textContent = text(event.category || event.kind, "event");
      const message = document.createElement("span");
      message.className = "event-message";
      message.textContent = text(event.message, "—");
      item.append(timeNode, kind, message);
      return item;
    }));
  }

  function renderEnvelope(envelope) {
    if (!envelope || typeof envelope !== "object" || !envelope.state) throw new Error("invalid telemetry envelope");
    const s = envelope.state;
    const age = Math.max(0, number(envelope.stateAgeSeconds, 9999));
    const live = s.synced && s.automationTransactionComplete && age <= 5;
    setConnection(live ? "live" : "stale", live
      ? `Snapshot #${number(s.decisionSequence).toLocaleString()} · ${age.toFixed(1)}s old · ${publicFeed ? "read-only laptop feed" : "local client"}`
      : `Latest snapshot is ${duration(age)} old or has not completed a synchronized transaction.`);
    byId("stale-banner").hidden = live;
    byId("stale-banner").textContent = live ? "" : `The latest ${publicFeed ? "laptop" : "local"} snapshot is stale or partial. Values below are retained for diagnosis and are not proof of current actions.`;
    renderHeadline(s);
    renderRoute(s);
    renderResources(s);
    renderCombatInventory(s);
    renderRebirth(s);
    renderTraining(s);
    renderEvents(envelope.events);
    lastSequence = number(s.decisionSequence, lastSequence);
  }

  async function poll() {
    try {
      const requestEndpoint = await discoverEndpoint();
      if (!requestEndpoint) throw new Error("waiting for endpoint discovery retry");
      const response = await fetch(requestEndpoint, { cache: "no-store", headers: { Accept: "application/json" } });
      if (!response.ok) throw new Error(`telemetry feed returned ${response.status}`);
      renderEnvelope(await response.json());
    } catch (error) {
      if (publicFeed && Date.now() >= nextEndpointDiscovery) endpoint = "";
      setConnection("offline", publicFeed
        ? "The laptop feed is unavailable. The laptop, game, and dashboard bridge must be running and awake."
        : "The bot dashboard bridge is not responding. Start the automation client.");
      byId("stale-banner").hidden = false;
      byId("stale-banner").textContent = "Live game state is unavailable; the static dashboard remains online. The bot is still the telemetry authority.";
    }
  }

  // The shared controller normally owns color mode. This fallback keeps the
  // loopback client usable when jehlp.net itself is temporarily unavailable.
  if (!document.documentElement.dataset.theme) {
    document.documentElement.dataset.theme = window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  poll();
  window.setInterval(poll, pollMs);
})();
