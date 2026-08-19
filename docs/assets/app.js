/*
FILE PURPOSE

This dependency-free client discovers and polls the laptop's current read-only public tunnel,
validates its telemetry envelope, and renders the full confirmed NGU state into a hierarchy led by
the selected Boss and record, ranked priorities, complete resource allocation, active growth,
Adventure outcomes, and inventory. Machine-level epoch/hash, root,
staged-authority, and shadow-scheduler diagnostics live in one collapsed disclosure. Missing
optional fields stay visibly unavailable rather than becoming false zero values or ETAs. Challenge
reset legality comes only from an active challenge plus challengeAllowsRebirth, never a negative
rebirth target. Held,
Pending, and Quarantined are separate states. Local copies use their own origin. The client sends no commands, persists no game data, and
exposes no mutation endpoint.
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
  const number = (value, fallback = 0) => value !== null && value !== undefined && value !== "" && Number.isFinite(Number(value)) ? Number(value) : fallback;
  const optionalNumber = (value) => value !== null && value !== undefined && value !== "" && Number.isFinite(Number(value)) ? Number(value) : null;
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

  function optionalDuration(seconds, unavailable = "Unavailable") {
    const value = optionalNumber(seconds);
    return value === null || value < 0 ? unavailable : duration(value, true);
  }

  function multiplierRatio(value) {
    const ratio = optionalNumber(value);
    if (ratio === null) return "Unavailable";
    if (ratio === 0) return "0×";
    if (ratio >= 0.01 && ratio < 1000) return `${compactDecimal(ratio, ratio >= 1 ? 3 : 6)}×`;
    return `${ratio.toExponential(3).replace("e+", "e")}×`;
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

  function stateToken(value) {
    const normalized = text(value, "unavailable").toLowerCase();
    if (normalized.includes("quarant")) return "quarantined";
    if (normalized.includes("error") || normalized.includes("abort")) return "error";
    if (normalized.includes("commit") || normalized.includes("complete")) return "committed";
    if (normalized.includes("available") && !normalized.includes("unavailable")) return "available";
    if (normalized.includes("pending") || normalized.includes("open") || normalized.includes("plan")
      || normalized.includes("reset") || normalized.includes("active") || normalized.includes("admitted")) return "pending";
    return "held";
  }

  function shortIdentity(value) {
    const normalized = text(value, "");
    return normalized ? normalized.slice(0, 12) : "Unavailable";
  }

  function confidence(value) {
    const amount = optionalNumber(value);
    return amount === null || amount < 0 || amount > 1 ? "Unavailable" : `${compactDecimal(amount * 100, 1)}%`;
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

  function fallbackObservability(s) {
    const target = optionalNumber(s.rebirthSeconds);
    const elapsed = optionalNumber(s.rebirthElapsed);
    const challengeAllowsRebirth = typeof s.challengeAllowsRebirth === "boolean" ? s.challengeAllowsRebirth : null;
    const challengeRulesSummary = text(s.challengeRulesSummary, "");
    const challengeRebirthPolicy = text(s.challengeRebirthPolicy, "");
    const challengeActive = typeof s.challengeActive === "boolean" ? s.challengeActive
      : typeof s.inChallenge === "boolean" ? s.inChallenge
        : String(s.stage || "").toLowerCase().includes("active challenge");
    const noResetChallenge = challengeActive && challengeAllowsRebirth === false;
    const hold = Boolean(s.rebirthExecutionHold) || noResetChallenge;
    const resetEta = !hold && target !== null && target >= 0 && elapsed !== null ? Math.max(0, target - elapsed) : null;
    const executionEnabled = typeof s.rebirthExecutionEnabled === "boolean" ? s.rebirthExecutionEnabled : null;
    const action = noResetChallenge ? "no-reset-challenge" : Boolean(s.rebirthExecutionHold) ? "hold"
      : executionEnabled === false ? "disabled" : resetEta === 0 ? "reset-due" : resetEta !== null ? "reset-at-checkpoint" : "unknown";
    const labels = {
      "no-reset-challenge": "NO RESET — this challenge forbids ordinary rebirths",
      hold: "HOLD — no executable reset is scheduled",
      disabled: "DISABLED — rebirth execution is off",
      "reset-due": "RESET DUE — waiting for the verified native boundary",
      "reset-at-checkpoint": "RESET at the selected checkpoint",
      unknown: "Rebirth decision telemetry is incomplete",
    };
    const previewRatios = [optionalNumber(s.rebirthProjectedAttackMultiplier), optionalNumber(s.rebirthProjectedDefenseMultiplier)].filter((value) => value !== null);
    const challengeAdmitted = typeof s.nextChallengeAdmitted === "boolean" ? s.nextChallengeAdmitted
      : typeof s.challengeAdmitted === "boolean" ? s.challengeAdmitted : false;
    const challengeName = s.nextChallengeName || s.challengeName || s.challengeType || s.challengeRecommendation;
    const challengeTargetBoss = optionalNumber(s.challengeTargetBoss);
    const challengeTargetLevel = optionalNumber(s.challengeTargetLevel);
    const root = s.mutationRoot && typeof s.mutationRoot === "object" ? s.mutationRoot : {};
    const rootId = optionalNumber(root.id);
    const rootEpoch = text(root.epochFingerprint, "");
    const decisionEpoch = text(s.gameEpochFingerprint, "");
    const rootEpochMatch = rootEpoch && decisionEpoch ? rootEpoch === decisionEpoch : null;
    const quarantined = number(root.quarantinedSteps) > 0 || String(root.state || "").toLowerCase().includes("quarant") || rootEpochMatch === false;
    const pending = number(root.pendingSteps) > 0 || ["open", "pending"].includes(String(root.state || "").toLowerCase());
    const transactionStatus = quarantined ? "Quarantined" : s.automationTransactionError ? "Error"
      : pending ? "Pending" : !rootId ? "Held" : s.automationTransactionComplete ? "Committed" : "Pending";
    const schedulerSource = s.globalScheduler && typeof s.globalScheduler === "object" ? s.globalScheduler : {};
    const nonnegative = (value) => { const parsed = optionalNumber(value); return parsed !== null && parsed >= 0 ? parsed : null; };
    const schedulerProvenance = text(schedulerSource.provenance, "");
    const schedulerSamples = nonnegative(schedulerSource.sampleCount);
    const schedulerConfidence = schedulerProvenance && schedulerProvenance.toLowerCase() !== "unknown"
      ? nonnegative(schedulerSource.confidence) : null;
    const routes = {};
    const staged = s.stagedAuthority && typeof s.stagedAuthority === "object" ? s.stagedAuthority : {};
    ["verifiedReversible", "permanentPurchases", "moneyPit", "challenges", "difficulty", "titan1Through12", "titan13Through14", "move69", "endSequence"].forEach((key) => {
      routes[key] = staged[key] === true ? "Enabled" : staged[key] === false ? "Held" : "Unavailable";
    });
    const inventoryTotal = nonnegative(s.inventoryTotalSlots);
    const inventoryFree = nonnegative(s.inventoryFreeSlots);
    const inventoryReserve = nonnegative(s.collectionRequiredFreeReserve);
    const capacityMargin = inventoryFree !== null && inventoryReserve !== null ? inventoryFree - inventoryReserve : null;
    const scheduler = {
      status: text(schedulerSource.status, "Unavailable"), authority: schedulerSource.authority || null,
      canExecute: typeof schedulerSource.canExecute === "boolean" ? schedulerSource.canExecute : null,
      snapshotHash: schedulerSource.snapshotHash || null, modelHash: schedulerSource.modelHash || null,
      objectiveHash: schedulerSource.objectiveHash || null, action: schedulerSource.action || null,
      actionId: schedulerSource.actionId || null, nextEvent: schedulerSource.nextEvent || null,
      eventId: schedulerSource.eventId || null, meanSeconds: nonnegative(schedulerSource.meanSeconds),
      p50Seconds: nonnegative(schedulerSource.p50Seconds), p90Seconds: nonnegative(schedulerSource.p90Seconds),
      lowerBoundSeconds: nonnegative(schedulerSource.lowerBoundSeconds), upperBoundSeconds: nonnegative(schedulerSource.upperBoundSeconds),
      gapSeconds: nonnegative(schedulerSource.gapSeconds), regretSeconds: nonnegative(schedulerSource.regretSeconds),
      blocker: schedulerSource.blocker || null, blockerDetail: schedulerSource.blockerDetail || null,
      provenance: schedulerProvenance && schedulerProvenance.toLowerCase() !== "unknown" ? schedulerProvenance : null,
      sampleCount: schedulerProvenance && schedulerProvenance.toLowerCase() !== "unknown" ? schedulerSamples : null,
      confidence: schedulerConfidence,
    };
    return {
      rebirth: {
        action,
        actionLabel: labels[action],
        reason: s.rebirthSafetyBlockReason || s.rebirthReason || "No decision reason was emitted.",
        noResetHold: hold || executionEnabled === false,
        targetRunAgeSeconds: target !== null && target >= 0 ? target : null,
        currentRunAgeSeconds: elapsed !== null && elapsed >= 0 ? elapsed : null,
        resetEtaSeconds: resetEta,
        nextPositiveEtaSeconds: optionalNumber(s.rebirthNextPositiveEtaSeconds),
        nextEvaluationEtaSeconds: optionalNumber(s.rebirthNextEvaluationEtaSeconds),
        etaReason: s.rebirthEtaReason || null,
        resetRecoveryEtaSeconds: optionalNumber(s.rebirthRecoveryResetRouteEtaSeconds),
        continueRecoveryEtaSeconds: optionalNumber(s.rebirthRecoveryContinueRouteEtaSeconds),
        selectedCycleRecoveryEtaSeconds: optionalNumber(s.rebirthOptimizerRecordRecoveryEtaSeconds),
        recoveryRemainingBosses: optionalNumber(s.rebirthRecoveryRemainingBosses),
        recoveryReason: s.rebirthRecoveryReason || s.rebirthOptimizerRecoveryReason || "",
        currentAttack: optionalNumber(s.rebirthCurrentAttackMultiplier),
        currentDefense: optionalNumber(s.rebirthCurrentDefenseMultiplier),
        previewAttack: optionalNumber(s.rebirthNextAttackMultiplierPreview),
        previewDefense: optionalNumber(s.rebirthNextDefenseMultiplierPreview),
        previewAttackRatio: optionalNumber(s.rebirthProjectedAttackMultiplier),
        previewDefenseRatio: optionalNumber(s.rebirthProjectedDefenseMultiplier),
        previewWorstRatio: previewRatios.length ? Math.min(...previewRatios) : null,
        selectedCheckpointWorstRatio: optionalNumber(s.rebirthMinimumNumberRatio),
        model: s.rebirthOptimizerModel || null,
        provenance: s.rebirthEtaProvenance || null,
        confidence: nonnegative(s.rebirthEtaConfidence),
      },
      challenge: {
        status: challengeActive ? "active" : challengeAdmitted ? "admitted" : "none-admitted",
        label: challengeName || (challengeActive ? "Active challenge (type unavailable)"
          : challengeAdmitted ? "Next admitted challenge" : "No challenge admitted"),
        admitted: challengeAdmitted,
        active: challengeActive,
        entryEtaSeconds: challengeActive ? 0 : challengeAdmitted ? resetEta : null,
        clearEtaSeconds: optionalNumber(s.nextChallengeEtaSeconds ?? s.challengeEtaSeconds),
        recoveryEtaSeconds: optionalNumber(s.challengeRecoveryEtaSeconds),
        targetBoss: challengeTargetBoss !== null && challengeTargetBoss >= 0 ? challengeTargetBoss : null,
        targetLevel: challengeTargetLevel !== null && challengeTargetLevel >= 0 ? challengeTargetLevel : null,
        reason: (challengeActive ? challengeRulesSummary : text(s.challengeEvidenceSummary, ""))
          || challengeRulesSummary || "The producer emitted no challenge-admission evidence.",
        allowsRebirth: challengeAllowsRebirth,
        rulesSummary: challengeRulesSummary || null,
        rebirthPolicy: challengeRebirthPolicy || null,
        provenance: s.challengeEtaProvenance || null,
        confidence: nonnegative(s.challengeEtaConfidence),
      },
      difficulty: {
        status: routes.difficulty === "Enabled" && (s.difficultyTarget || s.nextDifficulty || s.difficultyTransitionTarget)
          ? "Pending" : routes.difficulty === "Held" ? "Held" : "Unavailable",
        current: ["Normal", "Evil", "Sadistic"][number(s.difficulty, -1)] || null,
        target: s.difficultyTarget || s.nextDifficulty || s.difficultyTransitionTarget || null,
        etaSeconds: nonnegative(s.difficultyEtaSeconds ?? s.difficultyTransitionEtaSeconds),
        blocker: s.difficultyBlocker || s.difficultyTransitionReason || null,
        provenance: s.difficultyEtaProvenance || null,
        confidence: nonnegative(s.difficultyEtaConfidence),
      },
      end: {
        status: s.endgameReadyToTrigger === true && s.endgameExecutionAuthorized === true ? "Pending"
          : s.endgameExecutionAuthorized === false || s.endgameReadyToTrigger === false ? "Held" : "Unavailable",
        objective: s.endgameObjective || null, missing: s.endgameMissingSummary || null,
        titan12VersionTarget: nonnegative(s.endgameTitan12VersionTarget),
        ready: typeof s.endgameReadyToTrigger === "boolean" ? s.endgameReadyToTrigger : null,
        authorized: typeof s.endgameExecutionAuthorized === "boolean" ? s.endgameExecutionAuthorized : null,
        meanSeconds: scheduler.meanSeconds, p50Seconds: scheduler.p50Seconds,
        p90Seconds: scheduler.p90Seconds, lowerBoundSeconds: scheduler.lowerBoundSeconds,
        provenance: scheduler.provenance, confidence: scheduler.confidence,
      },
      identity: {
        verifiedEnvelope: false,
        decisionEnvelopeComplete: Boolean(s.buildId && s.producerSessionId && number(s.producerPid) > 0 && decisionEpoch),
        joinStatus: "Pending",
        deploymentDecisionMatch: false,
        rootEpochMatchesDecision: rootEpochMatch,
        buildId: s.buildId || null,
        producerPid: optionalNumber(s.producerPid),
        producerSessionId: s.producerSessionId || null,
        diskArtifactSha256: s.diskArtifactSha256 || null,
        gameAssemblySha256: s.gameAssemblySha256 || null,
        activeMatchesDisk: s.activeMatchesDisk || "unknown",
        decisionEpochFingerprint: decisionEpoch || null,
        deploymentEpochFingerprint: null,
        rootEpochFingerprint: rootEpoch || null,
      },
      transaction: {
        status: transactionStatus,
        complete: Boolean(s.automationTransactionComplete),
        error: s.automationTransactionError || null,
        rootId: rootId && rootId > 0 ? rootId : null,
        rootState: root.state || "Unavailable",
        rootEpochFingerprint: rootEpoch || null,
        committedSteps: nonnegative(root.committedSteps), pendingSteps: nonnegative(root.pendingSteps),
        rejectedSteps: nonnegative(root.rejectedSteps), quarantinedSteps: nonnegative(root.quarantinedSteps),
      },
      bindings: {
        status: s.nativeBindingsComplete === true && number(s.nativeBindingFailureCount, -1) === 0
          && number(s.nativeBindingDescriptorCount, -1) === number(s.nativeBindingBoundCount, -2)
          ? "Complete" : s.nativeBindingsComplete === false || number(s.nativeBindingFailureCount) > 0
            ? "Quarantined" : "Unavailable",
        knownBuild: typeof s.nativeBindingKnownBuild === "boolean" ? s.nativeBindingKnownBuild : null,
        complete: typeof s.nativeBindingsComplete === "boolean" ? s.nativeBindingsComplete : null,
        descriptorCount: nonnegative(s.nativeBindingDescriptorCount),
        boundCount: nonnegative(s.nativeBindingBoundCount),
        failureCount: nonnegative(s.nativeBindingFailureCount),
        failureSummary: s.nativeBindingFailureSummary || null,
        provenance: s.nativeBindingDescriptorCount === undefined ? null : "LoadedAssemblyMetadata",
      },
      authority: { stage: s.authorityStage || "Unavailable", routes },
      capacity: {
        status: inventoryTotal === null || inventoryFree === null ? "Unavailable" : capacityMargin !== null && capacityMargin < 0 ? "Held" : "Available",
        totalSlots: inventoryTotal, usedSlots: nonnegative(s.inventoryUsedSlots), freeSlots: inventoryFree,
        requiredReserve: inventoryReserve, projectedNewSlots: nonnegative(s.collectionProjectedNewSlots),
        marginSlots: capacityMargin, pressure: s.inventoryPressure || null,
        provenance: inventoryTotal !== null && inventoryFree !== null ? "LiveCounters" : null,
        confidence: inventoryTotal !== null && inventoryFree !== null ? 1 : null,
        exactDeliveryProof: typeof s.capacityProofExact === "boolean" ? s.capacityProofExact : null,
      },
      scheduler,
    };
  }

  function renderHeadline(s, observability) {
    const rebirth = observability.rebirth;
    const challenge = observability.challenge;
    const headline = rebirth.action === "reset-at-checkpoint" ? optionalDuration(rebirth.resetEtaSeconds)
      : rebirth.action === "reset-due" ? "Reset due"
        : rebirth.action === "no-reset-challenge" ? "No reset"
          : rebirth.action === "hold" ? "Planner hold"
            : rebirth.action === "disabled" ? "Disabled" : "Unavailable";
    setText("metric-rebirth", headline);
    setText("metric-rebirth-note", rebirth.actionLabel);

    setText("metric-challenge", challenge.active ? "Active" : challenge.admitted ? text(challenge.label) : "None admitted");
    setText("metric-challenge-note", challenge.active ? text(challenge.label) : challenge.admitted
      ? `entry ${optionalDuration(challenge.entryEtaSeconds)}` : text(challenge.reason, "admission evidence unavailable"));

    const selectedBoss = optionalNumber(s.bossSelectedId);
    const highestBoss = optionalNumber(s.highestBoss);
    const recordBoss = optionalNumber(s.bossRecordTargetId ?? s.nextBoss);
    setText("metric-boss-label", "Boss progress");
    setText("metric-boss", selectedBoss === null ? "Unavailable" : `#${selectedBoss.toLocaleString()}`);
    setText("metric-boss-record", highestBoss === null ? "Unavailable" : `#${highestBoss.toLocaleString()}`);
    const bossNote = recordBoss === null ? "next record unavailable" : `next record #${recordBoss.toLocaleString()}`;
    setText("metric-boss-note", `${bossNote} · target ETA ${optionalDuration(s.bossDefeatEtaSeconds, "unavailable")}`);

    setText("metric-adventure", s.adventureTargetName, "Selecting route");
    const routeMode = number(s.adventureTargetZone, -1) >= 1000 || number(s.adventureZone, -1) >= 1000
      ? `ITOPOD · ${text(s.itopodMode, "route selected")}`
      : s.majorUnlockActive ? `major unlock · ${s.majorUnlockName}`
      : s.collectionIsBackfill ? "MAXX backfill" : s.adventureBossOnlyForSet ? "boss-only collection" : "forward progression";
    setText("metric-adventure-note", routeMode);

    const expName = purchaseName(s, "exp");
    const expShortfall = number(s.expShortfall);
    setText("metric-exp", expShortfall > 0 ? `${shortNumber(expShortfall)} XP` : "Ready");
    setText("metric-exp-note", `${expShortfall > 0 ? "until" : "for"} ${expName}`);
  }

  function derivePriorities(s, observability) {
    const priorities = [];
    const challenge = observability.challenge;
    if (challenge.active) {
      const target = challenge.targetBoss === null || challenge.targetBoss === undefined
        ? text(challenge.rulesSummary, "complete its exact objective") : `reach Boss #${Number(challenge.targetBoss).toLocaleString()}`;
      priorities.push({ score: 100, tone: "challenge", title: `Finish ${text(challenge.label, "the active challenge")}`, detail: `${target}; ${text(challenge.rebirthPolicy, "follow the challenge's rebirth rule")}` });
    } else if (challenge.admitted) {
      priorities.push({ score: 96, tone: "challenge", title: `Prepare ${text(challenge.label, "the next challenge")}`, detail: `${text(challenge.reason, "The route has admission evidence")} · clear ${optionalDuration(challenge.clearEtaSeconds)}.` });
    }

    const itopodMode = text(s.itopodMode, "").toLowerCase();
    if (number(s.adventureTargetZone, -1) >= 1000 || itopodMode.includes("climb") || itopodMode.includes("farm")) {
      const targetFloor = Math.max(number(s.itopodRangeEnd), number(s.itopodNextAwardFloor));
      priorities.push({ score: itopodMode.includes("climb") ? 94 : 80, tone: "itopod", title: itopodMode.includes("climb") ? `Claim ITOPOD floor ${targetFloor}` : "Farm ITOPOD efficiently", detail: `${text(s.itopodRouteReason, "Earn PP and permanent Perks")} · floor ${number(s.itopodCurrentFloor)} now.` });
    }

    if (s.majorUnlockActive) priorities.push({ score: 91, tone: "route", title: `Unlock ${text(s.majorUnlockName, "the next system")}`, detail: text(s.majorUnlockReason || s.adventureControlReason, "Push the Adventure gate that opens the next mechanic.") });
    else if (!(number(s.adventureTargetZone, -1) >= 1000 || itopodMode.includes("climb") || itopodMode.includes("farm"))) priorities.push({ score: 78, tone: "route", title: text(s.loadoutObjective || s.adventureTargetName, "Keep progressing Adventure"), detail: text(s.adventureControlReason || s.loadoutDecision, "Use the strongest useful loadout for the selected route.") });

    if (!observability.rebirth.noResetHold && observability.rebirth.resetEtaSeconds !== null) {
      priorities.push({ score: 72, tone: "rebirth", title: "Rebirth at the selected checkpoint", detail: `${optionalDuration(observability.rebirth.resetEtaSeconds)} remaining · ${text(observability.rebirth.reason, "value checked at the boundary")}.` });
    }

    const expShortfall = number(s.expShortfall);
    priorities.push({ score: 60, tone: "purchase", title: expShortfall > 0 ? `Save ${shortNumber(expShortfall)} XP for ${purchaseName(s, "exp")}` : `Buy ${purchaseName(s, "exp")}`, detail: text(s.expDecision, "The next permanent purchase is continuously re-priced.") });
    return priorities.sort((a, b) => b.score - a.score).slice(0, 3);
  }

  function renderPriorities(s, observability) {
    const priorities = derivePriorities(s, observability);
    const list = byId("priority-list");
    list?.replaceChildren(...priorities.map((priority, index) => {
      const item = document.createElement("li");
      item.dataset.tone = priority.tone;
      const rank = document.createElement("span"); rank.className = "priority-rank"; rank.textContent = String(index + 1);
      const copy = document.createElement("div");
      const title = document.createElement("strong"); title.textContent = priority.title;
      const detail = document.createElement("p"); detail.textContent = sentence(priority.detail);
      copy.append(title, detail); item.append(rank, copy); return item;
    }));
    setText("priority-summary", "Ranked from active gates and verified live state; local utility is not a global speedrun proof.");
  }

  function renderRoute(s, observability) {
    const identity = observability.identity;
    setText("objective", s.objective, "Re-evaluating progression route.");
    setText("run-stage", `${text(s.stage, "unknown stage")} · ${text(s.syncState, "unsynchronized")}`);
    setText("route-title", s.loadoutObjective || s.objective, "Waiting for a synchronized transaction");
    setText("route-reason", sentence(s.adventureControlReason || s.loadoutDecision || s.objective));
    setText("fact-mode", `${text(s.mode, "unknown")} · ${s.mutationsEnabled ? "automation active" : "read only"}`);
    setText("fact-run-age", duration(s.rebirthElapsed));
    setText("fact-difficulty", ["Normal", "Evil", "Sadistic"][number(s.difficulty, -1)] || "Unavailable");
    setText("fact-selected-boss", optionalNumber(s.bossSelectedId) === null
      ? "Unavailable" : `Boss #${Number(s.bossSelectedId).toLocaleString()}`);
    setText("fact-snapshot", `#${number(s.decisionSequence).toLocaleString()}`);
    setText("fact-build", text(identity.buildId, "unavailable").slice(0, 12));
    setText("fact-disk", text(identity.diskArtifactSha256, "unavailable").slice(0, 12));
    setText("fact-game", text(identity.gameAssemblySha256, "unavailable").slice(0, 12));
    setText("fact-producer", identity.producerPid && identity.producerSessionId
      ? `${identity.producerPid.toLocaleString()} · ${identity.producerSessionId.slice(0, 12)}` : "unverified");
    setText("fact-identity", `${identity.verifiedEnvelope ? "epoch verified" : "epoch incomplete"} · ${text(identity.activeMatchesDisk, "disk match unknown").replaceAll("-", " ")}`);
  }

  function renderExecution(envelope, observability) {
    const identity = observability.identity;
    const transaction = observability.transaction;
    const scheduler = observability.scheduler;
    const bindings = observability.bindings || {};
    const capacity = observability.capacity;
    const authority = observability.authority;
    const actionTail = envelope.actionTail && typeof envelope.actionTail === "object" ? envelope.actionTail : {};
    const transactionToken = stateToken(transaction.status);
    const transactionCard = byId("transaction-card");
    if (transactionCard) transactionCard.dataset.state = transactionToken;
    setText("transaction-state", transaction.status, "Unavailable");
    setText("transaction-root-id", transaction.rootId === null || transaction.rootId === undefined ? "Unavailable" : `#${Number(transaction.rootId).toLocaleString()}`);
    setText("transaction-root-state", transaction.rootState, "Unavailable");
    const stepParts = [
      ["committed", transaction.committedSteps], ["pending", transaction.pendingSteps],
      ["rejected", transaction.rejectedSteps], ["quarantined", transaction.quarantinedSteps],
    ].filter(([, value]) => optionalNumber(value) !== null).map(([label, value]) => `${label} ${Number(value).toLocaleString()}`);
    setText("transaction-counts", stepParts.length ? stepParts.join(" · ") : "Unavailable");
    setText("decision-epoch", shortIdentity(identity.decisionEpochFingerprint));
    setText("root-epoch", identity.rootEpochFingerprint
      ? `${shortIdentity(identity.rootEpochFingerprint)} · ${identity.rootEpochMatchesDecision === true ? "matched" : identity.rootEpochMatchesDecision === false ? "mismatch" : "unverified"}`
      : "Unavailable");
    setText("action-tail-state", actionTail.status
      ? `${text(actionTail.status)}${actionTail.producerSessionId ? ` · ${shortIdentity(actionTail.producerSessionId)}` : ""}`
      : "Unavailable");
    setText("binding-state", `${text(bindings.status, "Unavailable")}${bindings.knownBuild === true ? " · audited build" : bindings.knownBuild === false ? " · unknown build" : ""}`);
    const bindingCounts = optionalNumber(bindings.descriptorCount) === null
      ? "Unavailable"
      : `${Number(bindings.boundCount || 0).toLocaleString()} / ${Number(bindings.descriptorCount).toLocaleString()} bound · ${Number(bindings.failureCount || 0).toLocaleString()} failures`;
    setText("binding-coverage", bindings.failureSummary ? `${bindingCounts} · ${bindings.failureSummary}` : bindingCounts);
    const capacityParts = capacity.freeSlots === null || capacity.freeSlots === undefined
      ? [] : [`${Number(capacity.freeSlots).toLocaleString()} free`];
    if (capacity.requiredReserve !== null && capacity.requiredReserve !== undefined) capacityParts.push(`reserve ${Number(capacity.requiredReserve).toLocaleString()}`);
    if (capacity.marginSlots !== null && capacity.marginSlots !== undefined) capacityParts.push(`margin ${Number(capacity.marginSlots).toLocaleString()}`);
    if (capacity.provenance) capacityParts.push(capacity.provenance);
    if (capacity.confidence !== null && capacity.confidence !== undefined) capacityParts.push(`${confidence(capacity.confidence)} observed-state confidence`);
    capacityParts.push(capacity.exactDeliveryProof === true ? "exact delivery proof" : capacity.exactDeliveryProof === false ? "delivery proof rejected" : "delivery proof unavailable");
    setText("capacity-state", `${text(capacity.status, "Unavailable")}${capacityParts.length ? ` · ${capacityParts.join(" · ")}` : ""}`);

    setText("execution-summary", `${text(identity.joinStatus, "Pending")} deployment/decision join · ${text(transaction.status, "Unavailable")} root`);
    const schedulerCard = byId("scheduler-card");
    if (schedulerCard) schedulerCard.dataset.state = scheduler.authority === "ShadowOnly" ? "held" : stateToken(scheduler.status);
    setText("scheduler-status", scheduler.status, "Unavailable");
    setText("scheduler-authority", `${text(scheduler.authority, "Unavailable")}${scheduler.canExecute === false ? " · cannot execute" : scheduler.canExecute === true ? " · executable" : ""}`);
    setText("scheduler-action", scheduler.action ? `${scheduler.action}${scheduler.actionId ? ` · ${scheduler.actionId}` : ""}` : "Unavailable");
    setText("scheduler-event", scheduler.nextEvent ? `${scheduler.nextEvent}${scheduler.eventId ? ` · ${scheduler.eventId}` : ""}` : "Unavailable");
    const provenanceParts = [];
    if (scheduler.provenance) provenanceParts.push(scheduler.provenance);
    if (scheduler.sampleCount !== null && scheduler.sampleCount !== undefined) provenanceParts.push(`${Number(scheduler.sampleCount).toLocaleString()} samples`);
    if (scheduler.confidence !== null && scheduler.confidence !== undefined) provenanceParts.push(`${confidence(scheduler.confidence)} confidence`);
    setText("scheduler-provenance", provenanceParts.length ? provenanceParts.join(" · ") : "Unavailable");
    const hashes = [scheduler.snapshotHash, scheduler.modelHash, scheduler.objectiveHash];
    setText("scheduler-hashes", hashes.some(Boolean) ? hashes.map(shortIdentity).join(" / ") : "Unavailable");
    setText("scheduler-mean", optionalDuration(scheduler.meanSeconds));
    setText("scheduler-p50", optionalDuration(scheduler.p50Seconds));
    setText("scheduler-p90", optionalDuration(scheduler.p90Seconds));
    setText("scheduler-lower", optionalDuration(scheduler.lowerBoundSeconds));
    setText("scheduler-gap", optionalDuration(scheduler.gapSeconds));
    setText("scheduler-regret", optionalDuration(scheduler.regretSeconds));
    setText("scheduler-blocker", scheduler.blocker
      ? `${scheduler.blocker}${scheduler.blockerDetail ? ` — ${scheduler.blockerDetail}` : ""}`
      : "No named scheduler blocker was emitted.");

    setText("authority-stage", authority.stage, "Unavailable");
    const labels = {
      verifiedReversible: "Verified reversible", permanentPurchases: "Permanent purchases",
      moneyPit: "Money Pit", challenges: "Challenges", difficulty: "Difficulty",
      titan1Through12: "Titans 1–12", titan13Through14: "Titans 13–14",
      move69: "MOVE69", endSequence: "END sequence",
    };
    const authorityList = byId("authority-list");
    const routes = authority.routes && typeof authority.routes === "object" ? authority.routes : {};
    authorityList?.replaceChildren(...Object.entries(labels).map(([key, label]) => {
      const item = document.createElement("li");
      const routeState = text(routes[key], "Unavailable");
      item.dataset.state = routeState.toLowerCase();
      const name = document.createElement("strong"); name.textContent = label;
      const state = document.createElement("span"); state.textContent = routeState;
      item.append(name, state);
      return item;
    }));

    const branches = [
      {
        key: "rebirth", state: observability.rebirth.actionLabel,
        status: observability.rebirth.action,
        eta: observability.rebirth.resetEtaSeconds,
        detail: observability.rebirth.model || observability.rebirth.provenance,
        confidence: observability.rebirth.confidence,
      },
      {
        key: "challenge", state: observability.challenge.label,
        status: observability.challenge.status,
        eta: observability.challenge.clearEtaSeconds,
        detail: observability.challenge.provenance,
        confidence: observability.challenge.confidence,
      },
      {
        key: "difficulty", state: observability.difficulty.target
          ? `${text(observability.difficulty.current, "Unavailable")} → ${observability.difficulty.target}`
          : `${text(observability.difficulty.current, "Unavailable")} · ${text(observability.difficulty.status, "Unavailable")}`,
        status: observability.difficulty.status,
        eta: observability.difficulty.etaSeconds,
        detail: observability.difficulty.blocker || observability.difficulty.provenance,
        confidence: observability.difficulty.confidence,
      },
      {
        key: "end", state: observability.end.status,
        status: observability.end.status,
        eta: observability.end.p90Seconds,
        detail: observability.end.missing || observability.end.provenance,
        confidence: observability.end.confidence,
      },
    ];
    for (const branch of branches) {
      const article = byId(`branch-${branch.key}`);
      if (article) article.dataset.state = stateToken(branch.status);
      setText(`branch-${branch.key}-state`, branch.state, "Unavailable");
      const etaLabel = branch.key === "end" ? "p90" : branch.key === "challenge" ? "clear" : "ETA";
      setText(`branch-${branch.key}-eta`, `${etaLabel} ${optionalDuration(branch.eta)}`);
      const proof = [];
      if (branch.detail) proof.push(branch.detail);
      if (branch.confidence !== null && branch.confidence !== undefined) proof.push(`${confidence(branch.confidence)} confidence`);
      setText(`branch-${branch.key}-proof`, proof.length ? proof.join(" · ") : "Provenance unavailable");
    }
  }

  function renderResources(s) {
    const energyAllocated = number(s.energyAllocated);
    const energyCurrent = number(s.energyCurrent);
    const magicAllocated = number(s.magicAllocated);
    const magicCurrent = number(s.magicCurrent);
    setText("resource-summary", `${shortNumber(energyAllocated + magicAllocated)} assigned · ${shortNumber(number(s.energyIdle) + number(s.magicIdle))} idle`);

    setText("energy-value", `${shortNumber(energyAllocated)} / ${shortNumber(energyCurrent)}`);
    setText("energy-rate", `+${shortNumber(s.energyIncomePerSecond)}/s`);
    setText("energy-decision", energyCurrent > 0 && number(s.energyIdle) <= 0
      ? "All Energy is assigned to productive systems." : sentence(s.energyIdleReason));
    byId("energy-meter").style.width = `${percent(energyAllocated, energyCurrent)}%`;
    setText("energy-bt", shortNumber(s.energyBasicTrainingAllocated));
    setText("energy-other", shortNumber(s.energyNonBasicTrainingAllocated));
    setText("energy-idle", shortNumber(s.energyIdle));
    renderAllocationList("energy-allocation-list", allocationGroups(s, "energy"), energyCurrent);

    setText("magic-value", `${shortNumber(magicAllocated)} / ${shortNumber(magicCurrent)}`);
    setText("magic-rate", `+${shortNumber(s.magicIncomePerSecond)}/s`);
    setText("magic-decision", magicCurrent > 0 && number(s.magicIdle) <= 0
      ? "All Magic is assigned to productive systems." : sentence(s.magicAllocationDecision));
    byId("magic-meter").style.width = `${percent(magicAllocated, magicCurrent)}%`;
    setText("magic-blood", shortNumber(s.magicBloodAllocated));
    setText("magic-tm", shortNumber(s.magicTimeMachineAllocated));
    setText("magic-idle", shortNumber(s.magicIdle));
    renderAllocationList("magic-allocation-list", allocationGroups(s, "magic"), magicCurrent);

    setText("exp-balance", `${shortNumber(s.exp)} XP`);
    setText("exp-decision", sentence(s.expDecision));
    setText("exp-eta", resourceEta(s, "exp"));
    setText("ap-balance", `${shortNumber(s.ap)} AP`);
    setText("ap-decision", sentence(s.apDecision));
    setText("ap-eta", `${shortNumber(s.apShortfall)} until ${purchaseName(s, "ap")}${number(s.apEtaSeconds, -1) >= 0 ? ` · ${duration(s.apEtaSeconds, true)}` : ""}`);
    setText("gold-balance", `${shortNumber(s.gold)} Gold`);
    setText("gold-decision", sentence(s.goldDecision));
    setText("gold-rate", `${shortNumber(s.goldIncomePerSecond)} net Gold/s`);

    const stats = s.characterStats || {};
    const res3Unlocked = Boolean(stats.res3Unlocked);
    setText("res3-name", stats.res3Name || "Resource 3");
    setText("res3-decision", res3Unlocked ? sentence(s.res3AllocationDecision || "Allocated to persistent Hacks and Wishes by marginal value") : "Not yet unlocked.");
    setText("res3-value", res3Unlocked ? `${shortNumber(stats.res3Current - stats.res3Idle)} / ${shortNumber(stats.res3Current)}` : "Locked");
    setText("res3-power", res3Unlocked ? shortNumber(stats.res3Power) : "—");
    setText("res3-cap", res3Unlocked ? shortNumber(stats.res3Cap) : "—");
    setText("res3-idle", res3Unlocked ? shortNumber(stats.res3Idle) : "—");
    byId("res3-meter").style.width = `${res3Unlocked ? percent(number(stats.res3Current) - number(stats.res3Idle), stats.res3Current) : 0}%`;
    renderAllocationList("res3-allocation-list", res3Unlocked ? allocationGroups(s, "resource3") : [], number(stats.res3Current));

    setText("pp-balance", `${shortNumber(s.itopodPerkPoints)} PP`);
    setText("pp-decision", sentence(s.itopodRouteReason));
    setText("pp-progress", `${shortNumber(s.itopodPointProgress)} / ${shortNumber(s.itopodPointThreshold)} · ${shortNumber(s.itopodProgressPerKill)} per kill`);
    setText("qp-balance", `${shortNumber(s.questPoints)} QP`);
    setText("qp-decision", s.questUnlocked ? sentence(s.questInProgress ? `Quest ${number(s.questId)} in progress` : "No active quest") : "Not yet unlocked.");
    setText("qp-progress", s.questUnlocked ? `${shortNumber(s.questQpPreview)} QP preview · ${number(s.questBanked)}/${number(s.questBankCap)} banked` : "Defeat the Beast to unlock quests");
    setText("seed-blood-balance", `${shortNumber(stats.seeds)} seeds · ${shortNumber(stats.blood)} blood`);
    setText("seed-blood-decision", s.yggFruitDecision || s.bloodMagicAllocationDecision, "Persistent-resource policy pending.");
    setText("seed-blood-detail", `${text(s.yggSeedDecision, "Yggdrasil locked")} · ${text(s.bloodMagicAllocationDecision, "Blood Magic locked")}`);
  }

  function allocationGroups(s, resource) {
    const summary = s.resourceAllocationSummary && typeof s.resourceAllocationSummary === "object" ? s.resourceAllocationSummary[resource] : null;
    if (summary && Array.isArray(summary.groups)) {
      const groups = summary.groups.filter((entry) => number(entry.allocated) > 0).map((entry) => ({ name: text(entry.name, "Other"), amount: number(entry.allocated) }));
      if (number(summary.idle) > 0 || !groups.length) groups.push({ name: "Idle", amount: number(summary.idle) });
      return groups;
    }
    if (resource === "energy") return [
      { name: "Basic Training", amount: number(s.energyBasicTrainingAllocated) },
      { name: "Other systems", amount: number(s.energyNonBasicTrainingAllocated) },
      { name: "Idle", amount: number(s.energyIdle) },
    ].filter((entry) => entry.amount > 0);
    if (resource === "magic") return [
      { name: "Wandoos", amount: number(s.magicWandoosAllocated) },
      { name: "Time Machine", amount: number(s.magicTimeMachineAllocated) },
      { name: "Blood Magic", amount: number(s.magicBloodAllocated) },
      { name: "Idle", amount: number(s.magicIdle) },
    ].filter((entry) => entry.amount > 0);
    return [];
  }

  function renderAllocationList(id, groups, capacity) {
    const list = byId(id);
    if (!list) return;
    if (!groups.length) {
      const empty = document.createElement("li"); empty.className = "allocation-empty"; empty.textContent = "No unlocked allocation sinks yet."; list.replaceChildren(empty); return;
    }
    list.replaceChildren(...groups.map((group) => {
      const item = document.createElement("li");
      const label = document.createElement("span"); label.textContent = group.name;
      const amount = document.createElement("strong"); amount.textContent = `${shortNumber(group.amount)} · ${compactDecimal(percent(group.amount, capacity), 1)}%`;
      const bar = document.createElement("i"); bar.style.width = `${percent(group.amount, capacity)}%`;
      item.append(label, amount, bar); return item;
    }));
  }

  function renderGrowth(s) {
    const setGrowth = (key, active, value, detail) => {
      const card = byId(`growth-${key}`); if (card) card.dataset.state = active ? "active" : "held";
      setText(`growth-${key}-value`, value); setText(`growth-${key}-detail`, detail);
    };
    const augmentEnergy = number(s.augmentEnergy);
    setGrowth("augment", augmentEnergy > 0, augmentEnergy > 0 ? `${shortNumber(augmentEnergy)} Energy` : "Waiting", `${text(s.augmentDecision, "No active Augment target")} · ${compactDecimal(number(s.augmentProgress) * 100, 1)}%${number(s.augmentEtaSeconds, -1) >= 0 ? ` · ${duration(s.augmentEtaSeconds, true)}` : ""}`);
    const wandoosTotal = number(s.wandoosEnergyAllocated) + number(s.magicWandoosAllocated);
    const osNames = ["Wandoos 98", "Wandoos MEH", "Wandoos XL"];
    setGrowth("wandoos", wandoosTotal > 0, wandoosTotal > 0 ? `${shortNumber(wandoosTotal)} E + M` : "Waiting", `${osNames[number(s.wandoosOsType)] || "Wandoos"} · Energy level ${shortNumber(s.wandoosEnergyLevel)} · Magic level ${shortNumber(s.wandoosMagicLevel)}`);
    const tmTotal = number(s.timeMachineEnergyAllocated) + number(s.magicTimeMachineAllocated);
    setGrowth("tm", tmTotal > 0, tmTotal > 0 ? `${shortNumber(tmTotal)} E + M` : "Waiting", `Speed level ${shortNumber(s.timeMachineSpeedLevel)} · Gold level ${shortNumber(s.timeMachineGoldLevel)} · ${text(s.timeMachineHorizonDecision, "horizon pending")}`);
    const atActive = number(s.advancedTrainingAttackTarget) + number(s.advancedTrainingDefenseTarget) > 0;
    setGrowth("at", atActive, atActive ? `${shortNumber(s.advancedTrainingAttackTarget)} A / ${shortNumber(s.advancedTrainingDefenseTarget)} D` : "Waiting", `${text(s.advancedTrainingHorizonDecision, "No reset-local target")} ${number(s.advancedTrainingCompletionEtaSeconds, -1) >= 0 ? `· ${duration(s.advancedTrainingCompletionEtaSeconds, true)}` : ""}`);
    const ngus = Array.isArray(s.nguProgress) ? s.nguProgress : [];
    const activeNgus = ngus.filter((entry) => number(entry.allocated) > 0);
    setGrowth("ngu", activeNgus.length > 0, activeNgus.length ? `${activeNgus.length} active` : "Waiting", activeNgus.length ? activeNgus.slice(0, 3).map((entry) => `${entry.name} ${shortNumber(entry.allocated)}`).join(" · ") : "No Energy or Magic is currently assigned to NGUs.");
    setText("growth-summary", `${[augmentEnergy > 0, wandoosTotal > 0, tmTotal > 0, atActive, activeNgus.length > 0].filter(Boolean).length} of 5 displayed systems active now.`);
  }

  function renderActivity(s, events) {
    const journal = byId("adventure-log-list");
    const records = Array.isArray(events) ? events : [];
    if (journal) journal.replaceChildren(...(records.length ? records.slice(0, 18).map((event) => {
      const item = document.createElement("li"); item.dataset.tone = text(event.tone, "route");
      const meta = document.createElement("span"); meta.textContent = `${text(event.clock, "—")} · ${text(event.category, "Adventure")}`;
      const message = document.createElement("p"); message.textContent = text(event.message, "—");
      item.append(meta, message); return item;
    }) : [(() => { const item = document.createElement("li"); item.dataset.tone = "route"; item.textContent = "No Adventure outcomes have been recorded in this session yet."; return item; })()]));
    setText("activity-summary", records.length ? `${records.length} useful outcomes from the current bot session · newest first` : "No current-session Adventure outcomes yet.");

    const inventory = Array.isArray(s.inventoryItems) ? s.inventoryItems : [];
    setText("inventory-glance-summary", `${number(s.inventoryUsedSlots)} used · ${number(s.inventoryFreeSlots)} free`);
    setText("inventory-glance-policy", `${text(s.inventoryPressure, "pressure unknown")} · ${text(s.boostDecision || s.loadoutDecision, "policy pending")}`);
    const chosen = [...inventory].sort((a, b) => Number(a.maxxed) - Number(b.maxxed) || number(b.level) - number(a.level)).slice(0, 7);
    const glance = byId("inventory-glance-list");
    glance?.replaceChildren(...(chosen.length ? chosen.map((item) => {
      const row = document.createElement("li");
      const name = document.createElement("span"); name.textContent = text(item.name, `Item #${number(item.id)}`);
      const state = document.createElement("strong"); state.textContent = `Lv ${number(item.level)}${item.maxxed ? " · MAXX" : " · leveling"}`;
      row.append(name, state); return row;
    }) : [(() => { const row = document.createElement("li"); row.textContent = "No physical inventory items."; return row; })()]));
  }

  function renderCombatInventory(s) {
    setText("combat-boss", `Boss #${number(s.bossSelectedId)}${s.bossTargetMatchesSelected ? " · record target" : ` · catching up to #${number(s.bossRecordTargetId)}`}`);
    setText("combat-eta", `${duration(s.bossDefeatEtaSeconds, true)} · ${text(s.bossEtaConfidence, "model pending")}`);
    setText("combat-zone", `${text(s.adventureTargetName, "—")} · ${text(s.adventureControlReason, "route pending")}`);
    setText("combat-stats", `${shortNumber(s.adventurePower)} / ${shortNumber(s.adventureToughness)}`);
    setText("combat-hp", `${shortNumber(s.adventureHP)} / ${shortNumber(s.adventureMaxHP)}`);
    setText("combat-collection", s.collectionMissingSummary || s.collectionReason, "—");
    setText("combat-reason", sentence(s.bossViabilityReason));

    const stats = s.characterStats || {};
    setText("fight-stats", `${shortNumber(stats.fightBossAttack)} / ${shortNumber(stats.fightBossDefense)}`);
    setText("fight-hp", `${shortNumber(stats.fightBossCurrentHP)} / ${shortNumber(stats.fightBossMaxHP)}`);
    setText("combat-meta", `${s.bossFighting ? "Fight Boss active" : "Fight Boss idle"} · ${text(s.nextTitanName, "Titan timing pending")}`);
    setText("itopod-route", `${text(s.itopodMode, "locked")} · ${text(s.itopodRouteReason, "route pending")}`);
    setText("itopod-floor", s.itopodRouteConfirmed ? `${number(s.itopodCurrentFloor)} / ${number(s.itopodHighestFloor)}` : "Not yet confirmed");
    setText("itopod-range", `${number(s.itopodRangeStart)}–${number(s.itopodRangeEnd)} · one-hit ${number(s.itopodReachableOneHitFloor)}`);
    setText("itopod-pp", `${shortNumber(s.itopodPointProgress)} / ${shortNumber(s.itopodPointThreshold)} · ${number(s.itopodKillsOnFloor)} kills on floor`);

    setText("gear-objective", s.loadoutObjective, "—");
    setText("inventory-space", `${number(s.inventoryUsedSlots)}/${number(s.inventoryTotalSlots)} used · ${number(s.inventoryFreeSlots)} free`);
    setText("inventory-pressure", text(s.inventoryPressure, "unknown"));
    setText("boost-target", sentence(s.boostDecision));
    setText("trash-policy", sentence(s.trashDecision));
    setText("gear-decision", sentence(s.loadoutDecision));
  }

  function renderRebirth(s, observability) {
    const rebirth = observability.rebirth;
    setText("rebirth-state", rebirth.actionLabel);
    setText("rebirth-reason", sentence(rebirth.reason));
    setText("rebirth-policy", rebirth.actionLabel);
    setText("rebirth-next-action", rebirth.action === "reset-at-checkpoint" ? "Execute verified native rebirth"
      : rebirth.action === "reset-due" ? "Verify and execute now"
        : rebirth.action === "no-reset-challenge" ? "Continue the active challenge"
          : rebirth.action === "hold" ? "Wait for a finite admitted checkpoint"
            : rebirth.action === "disabled" ? "Observe only" : "Await complete telemetry");
    setText("rebirth-reset-eta", optionalDuration(rebirth.resetEtaSeconds, rebirth.noResetHold ? "No reset scheduled" : "Unavailable"));
    setText("rebirth-hold", rebirth.noResetHold ? "Yes" : "No");
    setText("rebirth-current", rebirth.currentAttack === null || rebirth.currentDefense === null
      ? "Unavailable" : `${scientific(rebirth.currentAttack)} / ${scientific(rebirth.currentDefense)}`);
    setText("rebirth-preview", rebirth.previewAttack === null || rebirth.previewDefense === null
      ? "Unavailable" : `${scientific(rebirth.previewAttack)} / ${scientific(rebirth.previewDefense)}`);
    setText("rebirth-ratio", `${multiplierRatio(rebirth.previewAttackRatio)} / ${multiplierRatio(rebirth.previewDefenseRatio)}`);
    setText("rebirth-selected-ratio", multiplierRatio(rebirth.selectedCheckpointWorstRatio));
    setText("rebirth-reset-recovery", optionalDuration(rebirth.resetRecoveryEtaSeconds));
    setText("rebirth-continue-recovery", optionalDuration(rebirth.continueRecoveryEtaSeconds));
    setText("rebirth-cycle-recovery", optionalDuration(rebirth.selectedCycleRecoveryEtaSeconds));
    setText("rebirth-next-positive", optionalDuration(rebirth.nextPositiveEtaSeconds, rebirth.noResetHold ? "No finite candidate yet" : "Not needed"));
    setText("rebirth-next-evaluation", optionalDuration(rebirth.nextEvaluationEtaSeconds, "Every live control tick"));
    setText("rebirth-catchup", `${shortNumber(s.rebirthExpectedCatchupExp)} XP · ${shortNumber(s.rebirthExpectedCatchupExpPerHour)}/h`);
    setText("rebirth-ap", `${shortNumber(s.rebirthOptimizerProjectedAp || s.rebirthProjectedAp)} AP`);
    setText("rebirth-candidates", `${number(s.rebirthCandidateCount).toLocaleString()} · ${text(s.rebirthOptimizerModel, "model pending")}`);
    setText("rebirth-safety", sentence(rebirth.etaReason || rebirth.recoveryReason || s.rebirthSafetyBlockReason
      || (s.rebirthNumberNonRegression ? "Native Number is non-decreasing at this selected event boundary" : "Native Number loss is priced by the selected branch; it is not an execution prohibition")));
  }

  function renderChallenge(observability) {
    const challenge = observability.challenge;
    setText("challenge-state", challenge.active ? "active" : challenge.admitted ? "admitted" : "not admitted");
    setText("challenge-reason", sentence(challenge.reason));
    setText("challenge-name", challenge.label);
    setText("challenge-entry-eta", challenge.active ? "Already active" : optionalDuration(challenge.entryEtaSeconds));
    setText("challenge-clear-eta", optionalDuration(challenge.clearEtaSeconds));
    setText("challenge-recovery-eta", optionalDuration(challenge.recoveryEtaSeconds));
    setText("challenge-target-boss", challenge.targetLevel !== null && challenge.targetLevel !== undefined
      ? `Level ${challenge.targetLevel}` : challenge.targetBoss === null ? "Unavailable" : `Boss ${challenge.targetBoss}`);
    setText("challenge-admission", challenge.active ? "Native challenge state active"
      : challenge.admitted ? "Source-specific admission passed" : "Fail-closed; no entry scheduled");
    setText("challenge-rebirth-policy", challenge.rebirthPolicy
      || (challenge.allowsRebirth === true ? "Rebirth is allowed"
        : challenge.allowsRebirth === false ? "Rebirth is forbidden" : "Policy unavailable"));
    setText("challenge-rules", challenge.rulesSummary, "Rules unavailable");
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

  function renderTable(bodyId, records, mapRow, emptyMessage) {
    const body = byId(bodyId);
    if (!body) return;
    const rows = Array.isArray(records) ? records : [];
    if (!rows.length) {
      const tr = document.createElement("tr");
      const td = document.createElement("td");
      td.colSpan = body.closest("table")?.querySelectorAll("thead th").length || 1;
      td.textContent = emptyMessage;
      tr.append(td);
      body.replaceChildren(tr);
      return;
    }
    body.replaceChildren(...rows.map((record, index) => {
      const mapped = mapRow(record, index);
      const tr = document.createElement("tr");
      if (mapped.className) tr.className = mapped.className;
      for (const value of mapped.values) {
        const td = document.createElement("td");
        td.textContent = text(value);
        tr.append(td);
      }
      tr.dataset.search = mapped.values.join(" ").toLowerCase();
      return tr;
    }));
  }

  function specialSummary(item) {
    const specials = Array.isArray(item?.specials) ? item.specials : [];
    return specials.length ? specials.map((entry) => `${text(entry.type)} ${shortNumber(entry.current)}/${shortNumber(entry.cap)}`).join(", ") : "—";
  }

  function renderCharacter(s) {
    const stats = s.characterStats || {};
    setText("stat-fight-attack", shortNumber(stats.fightBossAttack));
    setText("stat-fight-defense", shortNumber(stats.fightBossDefense));
    setText("stat-fight-hp", `${shortNumber(stats.fightBossCurrentHP)} / ${shortNumber(stats.fightBossMaxHP)}`);
    setText("stat-adventure-power", shortNumber(stats.adventureAttack || s.adventurePower));
    setText("stat-adventure-toughness", shortNumber(stats.adventureDefense || s.adventureToughness));
    setText("stat-adventure-hp", `${shortNumber(stats.adventureCurrentHP || s.adventureHP)} / ${shortNumber(stats.adventureMaxHP || s.adventureMaxHP)}`);
    setText("stat-energy-pcb", `${shortNumber(stats.energyPower)} / ${shortNumber(stats.energyCap)} / ${shortNumber(stats.energyBars)}`);
    setText("stat-magic-pcb", `${shortNumber(stats.magicPower)} / ${shortNumber(stats.magicCap)} / ${shortNumber(stats.magicBars)}`);
    setText("stat-res3-label", `${text(stats.res3Name, "R3")} P / C / B`);
    setText("stat-res3-pcb", stats.res3Unlocked ? `${shortNumber(stats.res3Power)} / ${shortNumber(stats.res3Cap)} / ${shortNumber(stats.res3Bars)}` : "Not yet unlocked");

    const gear = Array.isArray(s.equippedGear) ? s.equippedGear : [];
    const equipped = gear.filter((item) => number(item.id) > 0);
    setText("gear-summary", `${equipped.length}/${gear.length} slots filled · ${text(s.loadoutObjective, "objective pending")}`);
    renderTable("gear-body", gear, (item) => ({
      className: number(item.id) > 0 ? "" : "is-locked",
      values: [item.slot, item.name, number(item.id) > 0 ? `${number(item.level)}${item.maxxed ? " · MAXX" : ""}` : "—", shortNumber(item.attack), shortNumber(item.defense), specialSummary(item)],
    }), "Gear telemetry is not available from the installed build.");
  }

  function renderInventory(s) {
    const inventory = Array.isArray(s.inventoryItems) ? s.inventoryItems : [];
    const daycare = Array.isArray(s.daycareItems) ? s.daycareItems : [];
    const macguffins = Array.isArray(s.macguffins) ? s.macguffins : [];
    const list = Array.isArray(s.itemListEntries) ? s.itemListEntries : [];
    setText("inventory-summary", `${inventory.length} physical items · ${number(s.inventoryFreeSlots)} free slots`);
    renderTable("inventory-body", inventory, (item) => ({
      className: item.maxxed ? "is-complete" : "",
      values: [number(item.index) + 1, item.name, item.part, `${number(item.level)}${item.maxxed ? " · MAXX" : ""}`, shortNumber(item.attack), shortNumber(item.defense), specialSummary(item), item.locked ? "Locked" : "Removable"],
    }), s.inventoryTotalSlots ? "Every physical inventory slot is empty." : "Inventory telemetry is not available from the installed build.");

    setText("daycare-summary", daycare.length ? `${daycare.length} occupied` : "empty or not yet unlocked");
    renderTable("daycare-body", daycare, (item) => ({ values: [number(item.index) + 1, item.name, `${number(item.level)}${item.maxxed ? " · MAXX" : ""}`, item.locked ? "Locked" : "Removable"] }), "No Item Daycare slots are occupied, or Daycare is not yet unlocked.");
    setText("macguffin-summary", macguffins.length ? `${macguffins.length} equipped` : "not yet unlocked or empty");
    renderTable("macguffin-body", macguffins, (item) => ({ values: [number(item.index) + 1, item.name, number(item.level), specialSummary(item)] }), "No MacGuffins are equipped; the system may not be unlocked yet.");

    const maxxed = list.filter((item) => item.maxxed).length;
    const catalog = number(s.itemListCatalogueCount, list.length);
    setText("item-list-summary", `${list.length}/${catalog} discovered · ${maxxed} MAXXED · ${Math.max(0, catalog - list.length)} unseen`);
    renderTable("item-list-body", list, (item) => ({
      className: item.maxxed ? "is-complete" : "",
      values: [item.id, item.name, item.dropped ? "Yes" : "No", item.maxxed ? "Yes" : "No", item.filtered ? "Yes" : "No"],
    }), "No Item List entries have been discovered yet.");
  }

  function renderPermanent(s) {
    const perks = Array.isArray(s.itopodPerks) ? s.itopodPerks : [];
    const boughtPerks = perks.filter((perk) => number(perk.level) > 0).length;
    setText("perks-summary", perks.length ? `${boughtPerks}/${perks.length} purchased · ${shortNumber(s.itopodPerkPoints)} PP available` : "not yet unlocked");
    renderTable("perks-body", perks, (perk) => ({
      className: !perk.unlocked ? "is-locked" : number(perk.level) >= number(perk.maxLevel) ? "is-complete" : "",
      values: [perk.id, perk.name, perk.type, `${shortNumber(perk.level)} / ${shortNumber(perk.maxLevel)}`, shortNumber(perk.baseCost), perk.unlocked ? (number(perk.level) >= number(perk.maxLevel) ? "MAXXED" : "Available") : `Not yet unlocked · difficulty ${number(perk.difficulty)}`],
    }), "ITOPOD perks are not yet unlocked.");

    const expPurchases = Array.isArray(s.expPurchases) ? s.expPurchases : [];
    setText("exp-purchases-summary", `${expPurchases.filter((entry) => entry.owned).length}/${expPurchases.length} active`);
    renderTable("exp-purchases-body", expPurchases, (entry) => ({ className: entry.owned ? "is-complete" : "is-locked", values: [entry.name, typeof entry.value === "number" ? shortNumber(entry.value) : entry.value, entry.owned ? "Bought / active" : "Not bought"] }), "No permanent EXP purchase telemetry is available.");

    const apPurchases = Array.isArray(s.apPurchases) ? s.apPurchases : [];
    setText("ap-purchases-summary", `${apPurchases.filter((entry) => entry.owned).length}/${apPurchases.length} bought`);
    renderTable("ap-purchases-body", apPurchases, (entry) => ({ className: entry.owned ? "is-complete" : "is-locked", values: [entry.id, entry.name, entry.owned ? "Bought" : entry.unlocked ? "Available" : "Not yet unlocked"] }), "No AP purchase telemetry is available.");

    const ngus = Array.isArray(s.nguProgress) ? s.nguProgress : [];
    setText("ngu-summary", ngus.length ? `${ngus.filter((entry) => entry.unlocked).length}/${ngus.length} tracks available` : "not yet unlocked");
    renderTable("ngu-body", ngus, (entry) => ({ className: entry.unlocked ? "" : "is-locked", values: [entry.resource, `${entry.name}${entry.unlocked ? "" : " · not yet unlocked"}`, shortNumber(entry.normalLevel), shortNumber(entry.evilLevel), shortNumber(entry.sadisticLevel), shortNumber(entry.allocated)] }), "NGUs are not yet unlocked.");

    const hacks = Array.isArray(s.hackProgress) ? s.hackProgress : [];
    setText("hacks-summary", hacks.length && hacks.some((entry) => entry.unlocked) ? `${hacks.length} tracks · ${hacks.filter((entry) => number(entry.allocated) > 0).length} active` : "not yet unlocked");
    renderTable("hacks-body", hacks, (entry) => ({ className: entry.unlocked ? "" : "is-locked", values: [entry.name, shortNumber(entry.level), shortNumber(entry.target), `${(number(entry.progress) * 100).toFixed(1)}%`, shortNumber(entry.allocated), entry.unlocked ? (number(entry.allocated) > 0 ? "Active" : "Idle") : "Not yet unlocked"] }), "Hacks are not yet unlocked.");

    const wishes = Array.isArray(s.wishProgress) ? s.wishProgress : [];
    setText("wishes-summary", wishes.length && wishes.some((entry) => entry.unlocked) ? `${wishes.filter((entry) => entry.unlocked).length}/${wishes.length} available` : "not yet unlocked");
    renderTable("wishes-body", wishes, (entry) => ({ className: !entry.unlocked ? "is-locked" : number(entry.level) >= number(entry.maxLevel) ? "is-complete" : "", values: [entry.id, entry.name, `${shortNumber(entry.level)} / ${shortNumber(entry.maxLevel)}`, `${(number(entry.progress) * 100).toFixed(1)}%`, `${shortNumber(entry.energy)} / ${shortNumber(entry.magic)} / ${shortNumber(entry.res3)}`, entry.unlocked ? (number(entry.level) >= number(entry.maxLevel) ? "MAXXED" : number(entry.energy) + number(entry.magic) + number(entry.res3) > 0 ? "Active" : "Available") : "Not yet unlocked"] }), "Wishes are not yet unlocked.");

    const fruits = Array.isArray(s.fruitProgress) ? s.fruitProgress : [];
    setText("fruit-summary", fruits.length && fruits.some((entry) => entry.unlocked) ? `${fruits.filter((entry) => entry.unlocked).length}/${fruits.length} unlocked` : "not yet unlocked");
    renderTable("fruit-body", fruits, (entry) => ({ className: entry.unlocked ? "" : "is-locked", values: [entry.name, entry.unlocked ? shortNumber(entry.maxTier) : "—", entry.unlocked ? shortNumber(entry.totalLevels) : "—", !entry.unlocked ? "Not yet unlocked" : entry.activated ? "Growing" : entry.permanentActivation ? "Permanent activation bought" : "Idle"] }), "Yggdrasil is not yet unlocked.");

    const diggers = Array.isArray(s.diggerProgress) ? s.diggerProgress : [];
    const beards = Array.isArray(s.beardProgress) ? s.beardProgress : [];
    setText("digger-beard-summary", `${diggers.filter((entry) => entry.unlocked).length} diggers · ${beards.filter((entry) => entry.unlocked).length} beards unlocked`);
    const combined = diggers.map((entry) => ({ system: "Digger", ...entry, permanent: entry.maxLevel }))
      .concat(beards.map((entry) => ({ system: "Beard", ...entry, permanent: entry.permanentLevel })));
    renderTable("digger-beard-body", combined, (entry) => ({ className: entry.unlocked ? "" : "is-locked", values: [entry.system, entry.name, entry.unlocked ? shortNumber(entry.level) : "—", entry.unlocked ? shortNumber(entry.permanent) : "—", !entry.unlocked ? "Not yet unlocked" : entry.active ? "Active" : "Idle"] }), "Diggers and Beards are not yet unlocked.");
  }

  function renderUnlocks(s) {
    const unlocks = Array.isArray(s.mechanicUnlocks) ? s.mechanicUnlocks : [];
    const list = byId("unlock-list");
    if (!unlocks.length) {
      list.innerHTML = "<li>Mechanic unlock telemetry is not available from the installed build.</li>";
      setText("unlocks-summary", "telemetry pending");
      return;
    }
    const unlocked = unlocks.filter((entry) => entry.unlocked).length;
    setText("unlocks-summary", `${unlocked}/${unlocks.length} systems unlocked`);
    list.replaceChildren(...unlocks.map((entry) => {
      const item = document.createElement("li");
      item.dataset.state = entry.unlocked ? "unlocked" : "locked";
      const name = document.createElement("strong");
      name.textContent = entry.name;
      const status = document.createElement("span");
      status.textContent = entry.unlocked ? "Unlocked" : "Not yet unlocked";
      const hint = document.createElement("small");
      hint.textContent = entry.unlocked ? "Available in this save" : entry.hint;
      item.append(name, status, hint);
      return item;
    }));
  }

  function refreshTableFilters() {
    document.querySelectorAll("[data-filter-target]").forEach((input) => {
      const query = input.value.trim().toLowerCase();
      const body = byId(input.dataset.filterTarget);
      body?.querySelectorAll("tr").forEach((row) => { row.hidden = Boolean(query) && !(row.dataset.search || row.textContent.toLowerCase()).includes(query); });
    });
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
      kind.textContent = `${text(event.category || event.kind, "event")}${event.importance ? ` · ${event.importance}` : ""}`;
      const message = document.createElement("span");
      message.className = "event-message";
      message.textContent = text(event.message, "—");
      item.append(timeNode, kind, message);
      return item;
    }));
  }

  function renderActionErrors(errors, observability) {
    const list = byId("action-error-list");
    const records = Array.isArray(errors) ? errors : [];
    const transactionError = observability.transaction.error;
    const displayed = transactionError ? [{
      clock: "now",
      category: "TRANSACTION",
      severity: "critical",
      message: transactionError,
      count: 1,
    }, ...records.filter((entry) => entry.message !== transactionError)] : records;
    setText("alerts-summary", displayed.length
      ? `${displayed.length} distinct recent signal${displayed.length === 1 ? "" : "s"}`
      : "no current action errors");
    if (!displayed.length) {
      list.innerHTML = "<li>No action failures or safety rejections are present in the current feed.</li>";
      return;
    }
    list.replaceChildren(...displayed.map((entry) => {
      const item = document.createElement("li");
      item.dataset.severity = text(entry.severity, "warning");
      const timeNode = document.createElement("span");
      timeNode.className = "event-time";
      timeNode.textContent = text(entry.clock, "—");
      const kind = document.createElement("span");
      kind.className = "event-kind";
      kind.textContent = `${text(entry.category, "error")}${number(entry.count, 1) > 1 ? ` ×${number(entry.count, 1)}` : ""}`;
      const message = document.createElement("span");
      message.className = "event-message";
      message.textContent = text(entry.message, "—");
      item.append(timeNode, kind, message);
      return item;
    }));
  }

  function renderEnvelope(envelope) {
    if (!envelope || typeof envelope !== "object" || !envelope.state) throw new Error("invalid telemetry envelope");
    const s = envelope.state;
    const fallback = fallbackObservability(s);
    const incoming = envelope.observability && typeof envelope.observability === "object" ? envelope.observability : {};
    const observability = {
      rebirth: { ...fallback.rebirth, ...(incoming.rebirth || {}) },
      challenge: { ...fallback.challenge, ...(incoming.challenge || {}) },
      identity: { ...fallback.identity, ...(incoming.identity || {}) },
      transaction: { ...fallback.transaction, ...(incoming.transaction || {}) },
      difficulty: { ...fallback.difficulty, ...(incoming.difficulty || {}) },
      end: { ...fallback.end, ...(incoming.end || {}) },
      authority: { ...fallback.authority, ...(incoming.authority || {}), routes: { ...fallback.authority.routes, ...(incoming.authority?.routes || {}) } },
      capacity: { ...fallback.capacity, ...(incoming.capacity || {}) },
      scheduler: { ...fallback.scheduler, ...(incoming.scheduler || {}) },
      bindings: { ...fallback.bindings, ...(incoming.bindings || {}) },
    };
    const age = Math.max(0, number(envelope.stateAgeSeconds, 9999));
    const live = s.synced && observability.transaction.complete && observability.identity.verifiedEnvelope && age <= 5;
    setConnection(live ? "live" : "stale", live
      ? `Snapshot #${number(s.decisionSequence).toLocaleString()} · ${age.toFixed(1)}s old · ${publicFeed ? "read-only laptop feed" : "local client"}`
      : `Latest snapshot is ${duration(age)} old, not transaction-complete, or outside the deployment/decision epoch.`);
    byId("stale-banner").hidden = live;
    byId("stale-banner").textContent = live ? "" : `The latest ${publicFeed ? "laptop" : "local"} snapshot is stale or partial. Values below are retained for diagnosis and are not proof of current actions.`;
    const errorBanner = byId("action-error-banner");
    const transactionAlert = ["error", "quarantined"].includes(stateToken(observability.transaction.status));
    errorBanner.hidden = !transactionAlert;
    errorBanner.textContent = transactionAlert
      ? `Latest automation transaction ${text(observability.transaction.status, "failure")}: ${text(observability.transaction.error, "inspect root epoch and quarantined-step counts")}` : "";
    renderHeadline(s, observability);
    renderPriorities(s, observability);
    renderRoute(s, observability);
    renderExecution(envelope, observability);
    renderResources(s);
    renderGrowth(s);
    renderActivity(s, envelope.adventureLog);
    renderCombatInventory(s);
    renderRebirth(s, observability);
    renderChallenge(observability);
    renderTraining(s);
    renderCharacter(s);
    renderInventory(s);
    renderPermanent(s);
    renderUnlocks(s);
    renderActionErrors(envelope.actionErrors, observability);
    renderEvents(envelope.events);
    refreshTableFilters();
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
  document.querySelectorAll("[data-filter-target]").forEach((input) => input.addEventListener("input", refreshTableFilters));
  poll();
  window.setInterval(poll, pollMs);
})();
