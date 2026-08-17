/*
FILE PURPOSE

ActionMonitor is a separate read-only macOS AppKit process. It tails confirmed actions and
schema-validated decision.json, rejects stale/build/PID/out-of-order telemetry, and renders status,
ETAs, collection debt, inventory pressure, and holds. It has no game handle or mutation path;
display features should follow explicit truthful producer fields.
*/
import AppKit

final class ActionMonitor: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let logPath: String
    private let decisionPath: String
    private let launchedAt = Date()
    private var offset: UInt64 = 0
    private var producerPid: Int?
    private var buildId: String?
    private var lastDecisionSequence = 0
    private var lastRenderedSequence = -1
    private var lastAcceptedModification = Date.distantPast
    private var producerEpoch = 0
    private var window: NSWindow!
    private var textView: NSTextView!
    private var goalsTextView: NSTextView!
    private var statusLabel: NSTextField!
    private var summaryLabel: NSTextField!
    private var timer: Timer?
    private let logLineRegex = try! NSRegularExpression(
        pattern: #"^(\d{2}:\d{2}:\d{2}\.\d{3}) (\[[^\]]+\]) (\([^\)]+\)) (.*)$"#)

    init(logPath: String, decisionPath: String) {
        self.logPath = logPath
        self.decisionPath = decisionPath
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)

        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1060, height: 720),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "NGU Idle Autopilot — Control Center"
        window.center()
        window.isReleasedWhenClosed = false
        window.delegate = self

        let root = NSView(frame: window.contentView!.bounds)
        root.autoresizingMask = [.width, .height]
        root.wantsLayer = true
        root.layer?.backgroundColor = NSColor(calibratedRed: 0.035, green: 0.045, blue: 0.065, alpha: 1).cgColor
        window.contentView = root

        statusLabel = NSTextField(labelWithString: "FULL AUTOMATION • CONNECTING")
        statusLabel.font = NSFont.monospacedSystemFont(ofSize: 15, weight: .bold)
        statusLabel.textColor = .systemGreen
        statusLabel.frame = NSRect(x: 18, y: root.bounds.height - 34, width: root.bounds.width - 36, height: 22)
        statusLabel.autoresizingMask = [.width, .minYMargin]
        root.addSubview(statusLabel)

        summaryLabel = NSTextField(labelWithString: "Waiting for verified live telemetry…")
        summaryLabel.font = NSFont.monospacedSystemFont(ofSize: 11.5, weight: .medium)
        summaryLabel.textColor = NSColor(calibratedWhite: 0.68, alpha: 1)
        summaryLabel.frame = NSRect(x: 18, y: root.bounds.height - 57, width: root.bounds.width - 36, height: 18)
        summaryLabel.autoresizingMask = [.width, .minYMargin]
        root.addSubview(summaryLabel)

        let divider = NSBox(frame: NSRect(x: 16, y: root.bounds.height - 65, width: root.bounds.width - 32, height: 1))
        divider.boxType = .separator
        divider.autoresizingMask = [.width, .minYMargin]
        root.addSubview(divider)

        let tabs = NSTabView(frame: NSRect(x: 12, y: 12, width: root.bounds.width - 24, height: root.bounds.height - 82))
        tabs.autoresizingMask = [.width, .height]
        root.addSubview(tabs)

        let actionsTab = NSTabViewItem(identifier: "actions")
        actionsTab.label = "Live Actions"
        let actionsScroll = makeScrollView(frame: tabs.contentRect)
        textView = makeTextView(frame: actionsScroll.bounds)
        actionsScroll.documentView = textView
        actionsTab.view = actionsScroll
        tabs.addTabViewItem(actionsTab)

        let goalsTab = NSTabViewItem(identifier: "goals")
        goalsTab.label = "Strategy & Goals"
        let goalsScroll = makeScrollView(frame: tabs.contentRect)
        goalsTextView = makeTextView(frame: goalsScroll.bounds)
        goalsTextView.font = NSFont.monospacedSystemFont(ofSize: 13, weight: .regular)
        goalsScroll.documentView = goalsTextView
        goalsTab.view = goalsScroll
        tabs.addTabViewItem(goalsTab)

        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        poll()
        timer = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: true) { [weak self] _ in self?.poll() }
    }

    private func makeScrollView(frame: NSRect) -> NSScrollView {
        let scroll = NSScrollView(frame: frame)
        scroll.autoresizingMask = [.width, .height]
        scroll.hasVerticalScroller = true
        scroll.borderType = .bezelBorder
        scroll.drawsBackground = true
        return scroll
    }

    private func makeTextView(frame: NSRect) -> NSTextView {
        let view = NSTextView(frame: frame)
        view.isEditable = false
        view.isSelectable = true
        view.isRichText = true
        view.isVerticallyResizable = true
        view.isHorizontallyResizable = false
        view.autoresizingMask = [.width]
        view.textContainer?.widthTracksTextView = true
        view.font = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        view.textColor = NSColor(calibratedWhite: 0.9, alpha: 1)
        view.backgroundColor = NSColor(calibratedWhite: 0.08, alpha: 1)
        view.textContainerInset = NSSize(width: 10, height: 10)
        view.insertionPointColor = .systemTeal
        return view
    }

    private func poll() {
        let fm = FileManager.default
        if let attrs = try? fm.attributesOfItem(atPath: logPath),
           let size = attrs[.size] as? NSNumber {
            let length = size.uint64Value
            if length < offset { offset = 0 }
            if length > offset, let handle = FileHandle(forReadingAtPath: logPath) {
                do {
                    try handle.seek(toOffset: offset)
                    let data = handle.readDataToEndOfFile()
                    offset += UInt64(data.count)
                    if let chunk = String(data: data, encoding: .utf8), !chunk.isEmpty {
                        textView.textStorage?.append(coloredLog(chunk))
                        if let storage = textView.textStorage, storage.length > 750_000 {
                            storage.deleteCharacters(in: NSRange(location: 0, length: min(150_000, storage.length)))
                        }
                        textView.scrollToEndOfDocument(nil)
                    }
                } catch { }
                try? handle.close()
            }
        }

        if let data = try? Data(contentsOf: URL(fileURLWithPath: decisionPath)),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let stage = object["stage"] as? String {
            let schema = number(object, "schemaVersion")
            let incomingPid = number(object, "producerPid")
            let incomingBuild = object["buildId"] as? String ?? ""
            let sequence = number(object, "decisionSequence")
            let attributes = try? FileManager.default.attributesOfItem(atPath: decisionPath)
            let modified = attributes?[.modificationDate] as? Date ?? .distantPast
            let age = max(0, Date().timeIntervalSince(modified))
            if schema != 2 || incomingPid <= 0 || incomingBuild.isEmpty || sequence <= 0 {
                statusLabel.stringValue = "AUTOMATION • REJECTED UNVERIFIED OR OUT-OF-SEQUENCE TELEMETRY"
                statusLabel.textColor = .systemRed
                return
            }

            /*
            PRODUCER-EPOCH HANDSHAKE

            A manually launched monitor may encounter a retained decision.json from an earlier
            process (the normal launcher proactively removes it). Older code latched that valid-
            looking PID/build and rejected the real producer forever. Do not bind an initial epoch
            until the file has changed after monitor launch.
            Once bound, accept a changed PID/build (or a small fresh sequence reset) only when the
            atomic file is newer than the last accepted frame and currently fresh. This heals
            legitimate restarts while still rejecting rollback/stale telemetry.
            */
            if producerPid == nil && modified < launchedAt.addingTimeInterval(-0.25) {
                statusLabel.stringValue = "AUTOMATION • WAITING FOR A FRESH PRODUCER FRAME"
                statusLabel.textColor = .systemOrange
                summaryLabel.stringValue = "Ignoring the previous process's retained decision file…"
                summaryLabel.textColor = .systemOrange
                return
            }
            let identityChanged = producerPid != nil
                && (producerPid != incomingPid || buildId != incomingBuild)
            let sequenceReset = producerPid != nil && sequence < lastDecisionSequence
            if identityChanged || sequenceReset {
                let validNewEpoch = age <= 5 && modified > lastAcceptedModification
                    && (identityChanged || sequence <= 10)
                if !validNewEpoch {
                    statusLabel.stringValue = "AUTOMATION • REJECTED STALE OR ROLLED-BACK TELEMETRY"
                    statusLabel.textColor = .systemRed
                    return
                }
                producerEpoch += 1
                lastDecisionSequence = 0
                lastRenderedSequence = -1
            }
            producerPid = incomingPid
            buildId = incomingBuild
            lastDecisionSequence = sequence
            if modified > lastAcceptedModification { lastAcceptedModification = modified }
            let elapsed = number(object, "rebirthElapsed")
            let synced = object["synced"] as? Bool ?? false
            let enabled = object["enabled"] as? Bool ?? false
            let mutationsEnabled = object["mutationsEnabled"] as? Bool ?? false
            let transactionComplete = object["automationTransactionComplete"] as? Bool ?? false
            let transactionError = object["automationTransactionError"] as? String ?? ""
            let mode = (object["mode"] as? String ?? "unknown").uppercased()
            if age > 5 {
                statusLabel.stringValue = "\(mode) AUTOMATION • STALE TELEMETRY \(Int(age))s • LAST \(stage.uppercased())"
                statusLabel.textColor = .systemRed
            } else if !enabled {
                statusLabel.stringValue = "AUTOMATION DISABLED • \(stage.uppercased())"
                statusLabel.textColor = .systemOrange
                summaryLabel.stringValue = "Automation is disabled; this is an observational producer heartbeat."
                summaryLabel.textColor = .systemOrange
            } else if !synced {
                statusLabel.stringValue = "\(mode) AUTOMATION PAUSED • NO GAMEPLAY MUTATIONS; AUTOSAVE LOAD MAY RUN"
                statusLabel.textColor = .systemOrange
                updateSummary(object)
            } else if !mutationsEnabled {
                statusLabel.stringValue = "\(mode) • OBSERVATION ONLY • NO GAMEPLAY MUTATIONS"
                statusLabel.textColor = .systemOrange
                summaryLabel.stringValue = "The producer is live, but mutation authority is not active."
                summaryLabel.textColor = .systemOrange
            } else if !transactionComplete {
                statusLabel.stringValue = "\(mode) • PARTIAL AUTOMATION CYCLE • SNAPSHOT #\(sequence)"
                statusLabel.textColor = .systemOrange
                summaryLabel.stringValue = transactionError.isEmpty
                    ? "A subsystem did not finish; the snapshot is current but the cycle was partial."
                    : "Partial cycle: \(transactionError)"
                summaryLabel.textColor = .systemOrange
            } else {
                let target = number(object, "rebirthSeconds")
                let remaining = max(0, target - elapsed)
                statusLabel.stringValue = "REBIRTH \(formatExactDuration(remaining))"
                statusLabel.textColor = .systemGreen
                updateSummary(object)
            }
            if sequence != lastRenderedSequence {
                lastRenderedSequence = sequence
                renderGoals(object)
            }
        } else {
            statusLabel.stringValue = "AUTOMATION • WAITING FOR BOT"
            statusLabel.textColor = .systemOrange
        }
    }

    private func updateSummary(_ state: [String: Any]) {
        guard state["synced"] as? Bool ?? false else {
            summaryLabel.stringValue = "SAFE PAUSE  •  no game mutations until active gameplay is verified"
            summaryLabel.textColor = .systemOrange
            return
        }
        let selectedBoss = number(state, "bossSelectedId")
        let bossEta = number(state, "bossDefeatEtaSeconds")
        let zone = state["adventureTargetName"] as? String ?? "selecting zone"
        let rebirthTarget = number(state, "rebirthSeconds")
        let rebirthElapsed = number(state, "rebirthElapsed")
        let rebirthRemaining = max(0, rebirthTarget - rebirthElapsed)
        let rebirthBlocked = !(state["rebirthExecutionEnabled"] as? Bool ?? true)
            || !(state["rebirthPreviewMonotonic"] as? Bool ?? true)
            || !(state["rebirthRecoveryResetEfficient"] as? Bool ?? true)
        let rebirthText = rebirthRemaining > 0 ? formatExactDuration(rebirthRemaining)
            : rebirthBlocked ? "route hold" : "now"
        let bossText = bossEta < 0 ? "ETA calculating" : "in " + formatEstimate(bossEta)
        statusLabel.stringValue = "REBIRTH \(rebirthText)   •   BOSS \(selectedBoss) \(bossText)"
        statusLabel.textColor = rebirthBlocked && rebirthRemaining <= 0 ? .systemOrange : .systemGreen

        let exp = numberDouble(state, "exp")
        let expTarget = numberDouble(state, "expTargetCost")
        let expShortfall = numberDouble(state, "expShortfall")
        let expTargetName = expPurchaseName(state["expDecision"] as? String ?? "")
        let expText: String
        if expTarget <= 0 {
            expText = "XP target: \(expTargetName)"
        } else if expShortfall <= 0 {
            expText = "XP READY for \(expTargetName) (\(shortNumber(exp))/\(shortNumber(expTarget)))"
        } else {
            expText = "XP \(shortNumber(expShortfall)) until \(expTargetName) (\(shortNumber(exp))/\(shortNumber(expTarget)))"
        }
        summaryLabel.stringValue = "ADVENTURE \(zone)   •   \(expText)"
        summaryLabel.textColor = .systemCyan
    }

    private func renderGoals(_ state: [String: Any]) {
        if !(state["synced"] as? Bool ?? false) {
            let detail = state["syncDetail"] as? String ?? "Waiting for the game to enter active gameplay."
            setColoredGoals("""
            SYNCHRONIZATION REQUIRED

            \(detail)

            The automation loops are hard-paused. Combat, allocations, inventory, purchases,
            Money Pit, bosses, Adventure, and rebirth actions cannot execute in this state.

            Full mode will use NGU Idle's own Load Autosave controller when a verified local
            save becomes available. Automation resumes only after MainMenuController reports
            completion and the main-menu transform is hidden by the game itself.
            """)
            return
        }
        let objective = state["objective"] as? String ?? "Re-evaluating"
        let highestBoss = number(state, "highestBoss")
        let nextBoss = max(highestBoss + 1, number(state, "nextBoss"))
        let selectedBoss = state["bossSelectedId"] == nil ? nextBoss : number(state, "bossSelectedId")
        let selectedMatchesRecord = state["bossTargetMatchesSelected"] as? Bool ?? true
        let bossReady = state["bossReady"] as? Bool ?? false
        let bossFighting = state["bossFighting"] as? Bool ?? false
        let bossKillETA = number(state, "bossKillEtaSeconds")
        let bossViabilityETA = state["bossDefeatEtaSeconds"] == nil
            ? number(state, "bossViabilityEtaSeconds") : number(state, "bossDefeatEtaSeconds")
        let bossFitsRebirth = state["bossDefeatFitsRebirthHorizon"] as? Bool ?? (bossViabilityETA >= 0)
        let bossRebirthSlack = number(state, "bossRebirthSlackSeconds")
        let bossViabilityReason = state["bossViabilityReason"] as? String ?? "waiting for the next exact combat viability result"
        let trainingGoal = state["trainingGoal"] as? String ?? "Speed-cap unlocked Basic Trainings"
        let trainingETA = number(state, "trainingEtaSeconds")
        let rebirthTarget = number(state, "rebirthSeconds")
        let rebirthElapsed = number(state, "rebirthElapsed")
        let rebirthRemaining = max(0, rebirthTarget - rebirthElapsed)
        let rebirthReason = state["rebirthReason"] as? String ?? "current highest-value checkpoint"
        let rebirthRunnerUp = number(state, "rebirthRunnerUpSeconds")
        let rebirthRunnerUpReason = state["rebirthRunnerUpReason"] as? String ?? "alternative checkpoint"
        let rebirthScore = numberDouble(state, "rebirthSelectedScorePerHour")
        let rebirthRunnerUpScore = numberDouble(state, "rebirthRunnerUpScorePerHour")
        let rebirthCandidates = state["rebirthCandidateSummary"] as? String ?? "candidate telemetry pending"
        let rebirthCandidateCount = number(state, "rebirthCandidateCount")
        let rebirthResolution = max(1, number(state, "rebirthSearchResolutionSeconds"))
        let rebirthHysteresis = numberDouble(state, "rebirthHysteresisPercent")
        let rebirthExecutionEnabled = state["rebirthExecutionEnabled"] as? Bool ?? true
        let rebirthPreviewMonotonic = state["rebirthPreviewMonotonic"] as? Bool ?? true
        let rebirthBossCatchupComplete = state["rebirthBossCatchupComplete"] as? Bool ?? true
        let rebirthRecoveryMode = state["rebirthRecoveryMode"] as? Bool ?? !rebirthBossCatchupComplete
        let rebirthRecoveryResetEfficient = state["rebirthRecoveryResetEfficient"] as? Bool ?? rebirthBossCatchupComplete
        let rebirthRecoveryResetETA = number(state, "rebirthRecoveryResetRouteEtaSeconds")
        let rebirthRecoveryContinueETA = number(state, "rebirthRecoveryContinueRouteEtaSeconds")
        let rebirthOptimizerRecoveryETA = number(state, "rebirthOptimizerRecordRecoveryEtaSeconds")
        let rebirthRecoveryRemainingBosses = number(state, "rebirthRecoveryRemainingBosses")
        let rebirthRecoveryReason = state["rebirthRecoveryReason"] as? String ?? "recovery route calculation pending"
        let rebirthSafetyBlockReason = state["rebirthSafetyBlockReason"] as? String ?? ""
        let adventureUnlocked = (state["adventureUnlocked"] as? Bool) ?? (highestBoss >= 4)
        let zone = state["adventureTargetName"] as? String ?? "best reachable zone"
        let fightType = number(state, "adventureFightType")
        let adventureBossOnly = state["adventureBossOnlyForSet"] as? Bool ?? false
        let power = numberDouble(state, "adventurePower")
        let toughness = numberDouble(state, "adventureToughness")
        let currentHP = numberDouble(state, "adventureHP")
        let maxHP = numberDouble(state, "adventureMaxHP")
        let recoveryReason = state["adventureRecoveryReason"] as? String ?? ""
        let recoveryETA = number(state, "adventureRecoveryEtaSeconds")
        let adventureControlReason = state["adventureControlReason"] as? String ?? ""
        let adventureSafeZoneSeconds = number(state, "adventureSafeZoneSeconds")
        let energyCurrent = numberDouble(state, "energyCurrent")
        let energyIdle = numberDouble(state, "energyIdle")
        let energyUtilization = numberDouble(state, "energyUtilization")
        let energyIdleReason = state["energyIdleReason"] as? String ?? "waiting-for-telemetry"
        let energyIncome = numberDouble(state, "energyIncomePerSecond")
        let basicTrainingEnergy = numberDouble(state, "energyBasicTrainingAllocated")
        let nonBasicTrainingEnergy = numberDouble(state, "energyNonBasicTrainingAllocated")
        let loadoutDecision = state["loadoutDecision"] as? String ?? "Evaluating owned equipment"
        let trashDecision = state["trashDecision"] as? String ?? "Conservative trash audit pending"
        let filterDecision = state["filterDecision"] as? String ?? "Collection-safe loot-filter audit pending"
        let collectionBackfill = state["collectionIsBackfill"] as? Bool ?? false
        let collectionRemaining = number(state, "collectionRemainingItems")
        let collectionZones = number(state, "collectionIncompleteZones")
        let collectionReason = state["collectionReason"] as? String ?? "Equipment collection planner pending"
        let collectionMissing = state["collectionMissingSummary"] as? String ?? "unknown equipment debt"
        let inventoryTotal = number(state, "inventoryTotalSlots")
        let inventoryUsed = number(state, "inventoryUsedSlots")
        let inventoryFree = number(state, "inventoryFreeSlots")
        let inventoryPressure = (state["inventoryPressure"] as? String ?? "unknown").uppercased()
        let yggSeedDecision = state["yggSeedDecision"] as? String ?? "Yggdrasil seed policy pending"
        let yggFruitDecision = state["yggFruitDecision"] as? String ?? "Yggdrasil fruit policy pending"
        let timeMachineHorizon = state["timeMachineHorizonDecision"] as? String
            ?? "Time Machine reset-horizon value is being evaluated"
        let allocationSummary: String
        if let rows = state["energyAllocationBreakdown"] as? [[String: Any]] {
            allocationSummary = rows.filter {
                numberDouble($0, "totalEnergy") > 0
                    || ($0["attackUnlocked"] as? Bool ?? false)
                    || ($0["defenseUnlocked"] as? Bool ?? false)
            }.map { row in
                let pair = row["pair"] as? String ?? "Training pair"
                let energy = numberDouble(row, "totalEnergy")
                let attackEnergy = numberDouble(row, "attackEnergy")
                let defenseEnergy = numberDouble(row, "defenseEnergy")
                let attackCap = numberDouble(row, "attackCap")
                let defenseCap = numberDouble(row, "defenseCap")
                let attackUnlocked = row["attackUnlocked"] as? Bool ?? true
                let defenseUnlocked = row["defenseUnlocked"] as? Bool ?? true
                let attackRate = numberDouble(row, "attackLevelsPerSecond")
                let defenseRate = numberDouble(row, "defenseLevelsPerSecond")
                let attackText = attackUnlocked
                    ? "A \(shortNumber(attackEnergy))/\(shortNumber(attackCap)) @ \(String(format: "%.2f", attackRate)) L/s"
                    : "Attack side locked"
                let defenseText = defenseUnlocked
                    ? "D \(shortNumber(defenseEnergy))/\(shortNumber(defenseCap)) @ \(String(format: "%.2f", defenseRate)) L/s"
                    : "Defense side locked"
                let selection = energy > 0
                    ? ""
                    : " — UNFUNDED: no immediate-gate win or reachable ≤2-run permanent cap-payback frontier"
                return "  • \(pair): \(shortNumber(energy)) E — \(attackText); \(defenseText)\(selection)"
            }.joined(separator: "\n")
        } else {
            allocationSummary = "  • Waiting for per-pair allocation telemetry"
        }

        let adventureStatus: String
        if !adventureUnlocked {
            adventureStatus = "Unlock Adventure by defeating Boss 4. Boss checks run 5 times/second."
        } else if number(state, "adventureZone") == -1 {
            let reason = adventureControlReason.isEmpty ? recoveryReason : adventureControlReason
            let eta = recoveryETA > 0 ? " — ETA \(formatEstimate(recoveryETA))" : ""
            adventureStatus = "Safe Zone for \(adventureSafeZoneSeconds)s: \(reason.lowercased()) (HP \(shortNumber(currentHP))/\(shortNumber(maxHP)))\(eta)."
        } else if adventureBossOnly && fightType == 2 {
            adventureStatus = "Boss-snipe \(zone) in ACTIVE fast-manual mode for its incomplete equipment set. Safe Zone hops are intentional full-respawn rerolls; combat resumes as soon as the next boss spawns. Collection: \(collectionMissing)."
        } else if adventureBossOnly {
            adventureStatus = "Boss-snipe \(zone) for its incomplete equipment set; Safe Zone waits are intentional spawn rerolls, not idle downtime."
        } else if fightType == 2 {
            adventureStatus = "Farm \(zone) in ACTIVE fast-manual mode now (P \(shortNumber(power)) / T \(shortNumber(toughness))). \(collectionBackfill ? "This is deliberate MAXX backfill." : collectionReason)"
        } else if fightType == 1 {
            adventureStatus = "Push \(zone) in ACTIVE tactical mode now: pre-buff, heal/block, then attack."
        } else {
            adventureStatus = "Hold the safest productive Adventure zone in IDLE mode while stats rise."
        }

        var shortTerm: [String] = []
        if trainingETA >= 0 && trainingETA <= 300 {
            shortTerm.append("\(trainingGoal) — ETA \(formatEstimate(trainingETA)).")
        }
        if bossFighting {
            shortTerm.append("Finish the active Fight Boss \(selectedBoss) attempt — ETA \(formatEstimate(bossKillETA)).")
        } else if bossReady {
            shortTerm.append("Defeat selected Fight Boss \(selectedBoss) — ready now.")
        } else {
            let bossEtaText: String
            if bossViabilityETA < 0 {
                bossEtaText = "no finite defeat ETA in the 60-minute projection window"
            } else if bossFitsRebirth {
                bossEtaText = "estimated defeat \(formatEstimate(bossViabilityETA)), fitting the rebirth by \(formatEstimate(max(0, bossRebirthSlack)))"
            } else {
                bossEtaText = "raw current-run defeat estimate \(formatEstimate(bossViabilityETA)), but it misses the selected rebirth by \(formatEstimate(abs(bossRebirthSlack)))"
            }
            let scope = selectedMatchesRecord ? "next-record progression" : "post-rebirth catch-up toward Boss \(nextBoss)"
            shortTerm.append("Selected Fight Boss \(selectedBoss) (\(scope)): \(bossViabilityReason); \(bossEtaText), ETA refreshed once per second and start eligibility checked five times per second.")
        }
        shortTerm.append(adventureStatus)
        if collectionRemaining > 0 {
            shortTerm.append("MAXX collection: \(collectionMissing) — \(collectionZones) fightable zone\(collectionZones == 1 ? "" : "s") still carry permanent Item List debt.")
        }
        if inventoryPressure == "HIGH" || inventoryPressure == "CRITICAL" {
            shortTerm.append("Protect loot capacity: only \(inventoryFree)/\(inventoryTotal) slots are free; safe trash/merge runs every second and AP space is promoted ahead of convenience purchases.")
        }
        if !rebirthExecutionEnabled || !rebirthPreviewMonotonic || !rebirthRecoveryResetEfficient {
            let reason = rebirthSafetyBlockReason.isEmpty
                ? "waiting for a strict Number improvement or a faster modeled recovery route" : rebirthSafetyBlockReason
            shortTerm.append("Rebirth route hold: \(reason).")
        } else if rebirthRemaining <= 300 {
            shortTerm.append("Execute the selected rebirth checkpoint — ETA \(formatEstimate(rebirthRemaining)).")
        }
        if state["questInProgress"] as? Bool ?? false {
            let current = number(state, "questCurrentDrops")
            let target = number(state, "questTargetDrops")
            let eta = number(state, "questEtaSeconds")
            let mode = (state["questIdle"] as? Bool ?? false) ? "idle alongside Adventure" : "active in its drop zone"
            shortTerm.append("Quest \(current)/\(target) drops, \(mode); QP preview \(number(state, "questQpPreview")) — ETA \(formatEstimate(eta)).")
        }
        if let nodes = state["goalNodes"] as? [[String: Any]] {
            for node in nodes {
                let eta = (node["etaSeconds"] as? NSNumber)?.intValue ?? -1
                let family = node["family"] as? String ?? ""
                let label = node["label"] as? String ?? ""
                guard eta >= 0 && eta <= 300 && family != "rebirth" && family != "training" else { continue }
                if !shortTerm.contains(where: { $0.contains(label) }) {
                    shortTerm.append("\(label) — ETA \(formatEstimate(eta)).")
                }
            }
        }
        shortTerm.append("Re-score Basic Training caps, Augment marginal value, drops, merging, boosts, and purchases on the next control tick.")
        if energyCurrent > 0 && energyIdle / energyCurrent > 0.01
            && energyIdleReason != "between-allocation-sweeps" && energyIdleReason != "sync-pair-remainder" {
            shortTerm.append("Resolve \(shortNumber(energyIdle)) idle Energy: \(energyIdleReason.replacingOccurrences(of: "-", with: " ")).")
        }

        var resourceDecisions: [String] = []
        appendResourceGoal(&resourceDecisions, state: state, amountKey: "exp", decisionKey: "expDecision", etaKey: "expEtaSeconds", label: "EXP")
        appendResourceGoal(&resourceDecisions, state: state, amountKey: "ap", decisionKey: "apDecision", etaKey: "apEtaSeconds", label: "AP")
        appendResourceGoal(&resourceDecisions, state: state, amountKey: "gold", decisionKey: "goldDecision", etaKey: "goldEtaSeconds", label: "gold")
        if state["magicAllocationDecision"] != nil {
            let magicDecision = state["magicAllocationDecision"] as? String ?? "waiting for a verified allocation sweep"
            resourceDecisions.append("Magic: \(magicDecision).")
        }
        let augmentDecision = state["augmentDecision"] as? String ?? "Re-evaluating Augments"
        let augmentETA = number(state, "augmentEtaSeconds")
        let augmentEnergy = numberDouble(state, "augmentEnergy")
        let augmentEtaText = augmentETA < 0 ? "no finite completion inside this run yet" : "ETA \(formatEstimate(augmentETA))"
        resourceDecisions.append("Augmentation: \(augmentDecision); Energy \(shortNumber(augmentEnergy)) — \(augmentEtaText).")

        let numberedShort = shortTerm.enumerated()
            .map { "\($0.offset + 1). \($0.element)" }
            .joined(separator: "\n")
        let longerTerm = longerTermGoals(state: state, highestBoss: highestBoss)
            .map { "• \($0)" }
            .joined(separator: "\n")
        let resourceBody = resourceDecisions.isEmpty
            ? "No held spendable resources."
            : resourceDecisions.map { "• \($0)" }.joined(separator: "\n")
        let rewardForecast: String
        if state["rebirthProjectedAttackMultiplier"] != nil {
            let attackGain = numberDouble(state, "rebirthProjectedAttackMultiplier")
            let defenseGain = numberDouble(state, "rebirthProjectedDefenseMultiplier")
            let safety = rebirthPreviewMonotonic ? "MONOTONIC" : "BLOCKED: WOULD DECREASE NUMBER"
            rewardForecast = "Native rebirth preview: Attack ×\(String(format: "%.4g", attackGain)), Defense ×\(String(format: "%.4g", defenseGain)) [\(safety)]; selected checkpoint yields \(number(state, "rebirthProjectedAp")) time-based AP before Titan bonuses."
        } else {
            rewardForecast = "Forecast reward: live multiplier/AP projection will appear after the next safe controller reload."
        }
        let optimizerForecast: String
        let rankedCandidates = "  • " + rebirthCandidates.replacingOccurrences(of: " | ", with: "\n  • ")
        if rebirthScore > 0 {
            let advantage = rebirthRunnerUpScore > 0
                ? 100.0 * (rebirthScore / rebirthRunnerUpScore - 1.0) : 0.0
            let scoreUnit = rebirthRecoveryMode ? "record-recovery progress/hour" : "log-growth/hour"
            optimizerForecast = """
            WINNER SCORE: \(String(format: "%.6f", rebirthScore)) \(scoreUnit)
            RUNNER-UP: \(formatExactDuration(rebirthRunnerUp)) — \(String(format: "%.6f", rebirthRunnerUpScore))/hour
            ADVANTAGE: \(String(format: "%.3f", advantage))% over the next-best evaluated second
            RUNNER-UP REASON: \(rebirthRunnerUpReason)
            SEARCH: \(groupedInteger(rebirthCandidateCount)) legal timings at \(rebirthResolution)-second resolution; \(String(format: "%.2f", rebirthHysteresis))% anti-jitter hysteresis
            NAMED MECHANICS CANDIDATES:
            \(rankedCandidates)
            """
        } else {
            optimizerForecast = "Optimizer detail: this progression stage is still governed by a mandatory Titan, puzzle, or long-cycle checkpoint."
        }

        let recoveryForecast: String
        if rebirthRecoveryMode {
            let resetEtaText = rebirthRecoveryResetETA >= 0 ? formatEstimate(rebirthRecoveryResetETA) : "unavailable"
            let continueEtaText = rebirthRecoveryContinueETA >= 0 ? formatEstimate(rebirthRecoveryContinueETA) : "no finite current-run route"
            recoveryForecast = """
            RECORD RECOVERY MODE: \(rebirthRecoveryRemainingBosses) catch-up boss transitions remain.
            SELECTED REPEATED-CYCLE POLICY ETA: \(rebirthOptimizerRecoveryETA >= 0 ? formatEstimate(rebirthOptimizerRecoveryETA) : "still projecting")
            RESET + REPLAY ROUTE: \(resetEtaText)
            CONTINUE CURRENT RUN ROUTE: \(continueEtaText)
            ROUTE VERDICT: \(rebirthRecoveryReason)
            """
        } else {
            recoveryForecast = "RECORD RECOVERY MODE: complete; ordinary long-run growth objective applies."
        }

        let roundExplanation = rebirthReason.lowercased().contains("exact")
            ? "WHY THE ROUND NUMBER: this second is a discontinuity in NGU Idle's native Number time-multiplier formula; it was evaluated, not assumed."
            : "WHY THIS SECOND: it is the highest-scoring live event or integer-second candidate in the current finite-horizon model."
        let bossGlance = bossReady ? "READY NOW"
            : bossViabilityETA < 0 ? "ETA CALCULATING" : "ETA " + formatEstimate(bossViabilityETA)

        let body = """
        AT A GLANCE
        ▶ NEXT: \(shortTerm.first ?? "Re-evaluating the next verified action.")
        ◆ BOSS: selected \(selectedBoss) / next record \(nextBoss) — \(bossGlance)
        ◆ ADVENTURE: \(zone) — \(collectionBackfill ? "MAXX BACKFILL" : "FORWARD COLLECTION")
        ◆ INVENTORY: \(inventoryUsed)/\(inventoryTotal) used, \(inventoryFree) free — \(inventoryPressure) PRESSURE
        ◆ REBIRTH: \(rebirthExecutionEnabled && rebirthPreviewMonotonic && rebirthRecoveryResetEfficient ? formatExactDuration(rebirthRemaining) + " remaining — exact target " + formatExactDuration(rebirthTarget) : "ROUTE HOLD — " + rebirthSafetyBlockReason)

        REBIRTH DECISION — LIVE MODEL
        TARGET RUN AGE: \(formatExactDuration(rebirthTarget))
        CURRENT RUN AGE: \(formatExactDuration(rebirthElapsed))
        REMAINING: \(formatExactDuration(rebirthRemaining))
        EXPECTED EXECUTION: \(wallClockEstimate(rebirthRemaining)) local time
        \(roundExplanation)
        SELECTION REASON: \(rebirthReason)
        \(optimizerForecast)
        \(recoveryForecast)

        CURRENT STRATEGY
        \(objective)
        \(rewardForecast)

        SHORT TERM — NEXT FEW MINUTES
        \(numberedShort)

        LONGER TERM — NEXT PROGRESSION GATES
        \(longerTerm)

        LIVE RESOURCE DECISIONS
        \(resourceBody)

        LIVE PROGRESS
        Highest Fight Boss: \(highestBoss)    Next record: \(nextBoss)    Selected in this run: \(selectedBoss)
        Adventure target: \(zone)
        Equipment collection: \(collectionReason); \(collectionMissing). Remaining debt markers \(collectionRemaining) across \(collectionZones) fightable zone\(collectionZones == 1 ? "" : "s").
        Inventory capacity: \(inventoryUsed)/\(inventoryTotal) used, \(inventoryFree) free — \(inventoryPressure.lowercased()) pressure. Space purchases are dynamically promoted when the merge/drop reserve is threatened.
        Adventure stats: Power \(shortNumber(power)) / Toughness \(shortNumber(toughness))
        Energy: \(shortNumber(max(0, energyCurrent - energyIdle))) allocated / \(shortNumber(energyCurrent)) total (\(String(format: "%.1f", 100 * energyUtilization))% utilized); +\(shortNumber(energyIncome))/s; idle state: \(energyIdleReason.replacingOccurrences(of: "-", with: " "))
        Reconciliation: \(shortNumber(basicTrainingEnergy)) E in Basic Training; \(shortNumber(nonBasicTrainingEnergy)) E in Augments/other Energy systems; \(shortNumber(energyIdle)) E idle.
        Training allocation:
        \(allocationSummary)
        Long-horizon BT rule: \(state["basicTrainingLongHorizonPolicy"] as? String ?? "persistent cap investments are evaluated before immediate boss marginal value")
        Time Machine horizon: \(timeMachineHorizon)
        Equipment: \(loadoutDecision)
        Inventory reclamation: \(trashDecision)
        Loot-filter safety: \(filterDecision)
        Yggdrasil seeds: \(yggSeedDecision)
        Yggdrasil harvest: \(yggFruitDecision)
        """

        setColoredGoals(body)
    }

    private func coloredLog(_ chunk: String) -> NSAttributedString {
        let output = NSMutableAttributedString()
        chunk.enumerateLines { line, _ in
            let paragraph = NSMutableParagraphStyle()
            paragraph.lineSpacing = 1.5
            let row = NSMutableAttributedString(string: line + "\n", attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .regular),
                .foregroundColor: NSColor(calibratedWhite: 0.9, alpha: 1),
                .paragraphStyle: paragraph
            ])
            let nsLine = line as NSString
            let fullRange = NSRange(location: 0, length: nsLine.length)
            if let match = self.logLineRegex.firstMatch(in: line, range: fullRange) {
                let category = nsLine.substring(with: match.range(at: 2)).uppercased()
                let categoryColor = self.logColor(category)
                row.addAttributes([.foregroundColor: NSColor(calibratedWhite: 0.48, alpha: 1)],
                                  range: match.range(at: 1))
                row.addAttributes([
                    .foregroundColor: categoryColor,
                    .backgroundColor: categoryColor.withAlphaComponent(0.13),
                    .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .bold)
                ], range: match.range(at: 2))
                row.addAttributes([.foregroundColor: NSColor(calibratedWhite: 0.58, alpha: 1)],
                                  range: match.range(at: 3))
                if category == "[ALLOC]" {
                    row.addAttributes([.foregroundColor: NSColor(calibratedWhite: 0.66, alpha: 1)],
                                      range: match.range(at: 4))
                }
                let confirmed = nsLine.range(of: "[confirmed", options: [.caseInsensitive])
                if confirmed.location != NSNotFound {
                    row.addAttributes([.foregroundColor: NSColor.systemGreen,
                                       .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .medium)],
                                      range: NSRange(location: confirmed.location,
                                                     length: nsLine.length - confirmed.location))
                }
                let eta = nsLine.range(of: "ETA ", options: [.caseInsensitive])
                if eta.location != NSNotFound {
                    row.addAttribute(.foregroundColor, value: NSColor.systemYellow,
                                     range: NSRange(location: eta.location, length: nsLine.length - eta.location))
                }
            }
            output.append(row)
        }
        return output
    }

    private func logColor(_ category: String) -> NSColor {
        if category == "[REJECTED]" || category == "[ERROR]" || category == "[DEATH]" { return .systemRed }
        if category == "[HOLD]" || category == "[RECOVERY]" { return .systemOrange }
        if category == "[REBIRTH]" || category == "[PROGRESSION]" || category == "[SYNC]" { return .systemPurple }
        if category == "[BOSS]" || category == "[COMBAT]" || category == "[TITAN]" { return .systemPink }
        if category == "[PURCHASE]" || category == "[LOOT]" || category == "[TRASH]"
            || category == "[INVENTORY]" || category == "[GEAR]" || category == "[COLLECTION]" { return .systemTeal }
        if category == "[ALLOC]" { return .systemBlue }
        if category == "[REWARD]" { return .systemYellow }
        return NSColor(calibratedWhite: 0.78, alpha: 1)
    }

    private func setColoredGoals(_ body: String) {
        let output = NSMutableAttributedString()
        body.enumerateLines { line, _ in
            let upper = line.uppercased()
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            let paragraph = NSMutableParagraphStyle()
            paragraph.lineSpacing = 2
            paragraph.paragraphSpacing = trimmed.isEmpty ? 3 : 0
            var color = NSColor(calibratedWhite: 0.9, alpha: 1)
            var weight: NSFont.Weight = .regular
            var size: CGFloat = 13
            if !trimmed.isEmpty && trimmed == upper && !trimmed.hasPrefix("•") {
                color = .systemPurple; weight = .bold; size = 14
            } else if trimmed.hasPrefix("▶") {
                color = .systemGreen; weight = .semibold; size = 13.5
            } else if trimmed.hasPrefix("◆") {
                color = .systemCyan; weight = .medium
            } else if upper.contains("REBIRTH") || upper.contains("OPTIMIZER") || upper.contains("ETA") {
                color = .systemYellow; weight = .medium
            }
            if upper.contains("BLOCKED") || upper.contains("MISSES") || upper.contains("NO FINITE") || upper.contains("REJECTED") {
                color = .systemOrange; weight = .semibold
            } else if upper.contains("WINNER") || upper.contains("ADVANTAGE") {
                color = .systemGreen; weight = .semibold
            } else if upper.contains("RUNNER-UP") || upper.contains("SEARCH:") {
                color = .systemBlue; weight = .medium
            } else if upper.contains("WHY THE ROUND NUMBER") || upper.contains("TARGET RUN AGE") || upper.contains("EXPECTED EXECUTION") {
                color = .systemYellow; weight = .semibold
            } else if upper.contains("READY NOW") || upper.contains("SYNCED") || upper.contains("COMPLETE") || upper.contains("FULLY-ALLOCATED") {
                color = .systemGreen; weight = .medium
            } else if upper.contains("EXP ") || upper.contains("AP ") || upper.contains("GOLD ") || upper.contains("ENERGY:") || upper.contains("MAGIC:") || upper.contains("AUGMENTATION:") {
                color = .systemTeal
            } else if upper.contains("INVENTORY") || upper.contains("COLLECTION") || upper.contains("MAXX") {
                color = upper.contains("CRITICAL") || upper.contains("HIGH PRESSURE") ? .systemOrange : .systemCyan
            } else if upper.contains("YGGDRASIL") || upper.contains("SEEDS:") {
                color = .systemGreen
            } else if upper.contains("BOSS") || upper.contains("ADVENTURE") || upper.contains("TITAN") {
                color = .systemPink
            }
            output.append(NSAttributedString(string: line + "\n", attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: size, weight: weight),
                .foregroundColor: color,
                .paragraphStyle: paragraph
            ]))
        }
        goalsTextView.textStorage?.setAttributedString(output)
    }

    private func appendResourceGoal(_ goals: inout [String], state: [String: Any], amountKey: String,
                                    decisionKey: String, etaKey: String, label: String) {
        let amount = numberDouble(state, amountKey)
        guard amount > 0 else { return }
        let decision = state[decisionKey] as? String ?? "Re-evaluating spend options"
        let eta = number(state, etaKey)
        let stateName = state["\(amountKey)State"] as? String ?? "evaluating"
        let target = numberDouble(state, "\(amountKey)TargetCost")
        let shortfall = numberDouble(state, "\(amountKey)Shortfall")
        let rate = numberDouble(state, "\(amountKey)IncomePerSecond")
        let etaText = shortfall <= 0
            ? "funded/available now"
            : eta < 0 ? "ETA unavailable until income resumes" : "ETA \(formatEstimate(eta))"
        let targetText = target > 0
            ? "; target \(shortNumber(target)), shortfall \(shortNumber(shortfall))"
            : ""
        let rateText = rate > 0 ? "; measured income \(shortNumber(rate))/s" : ""
        goals.append("\(label) \(shortNumber(amount)) [\(stateName)]: \(decision)\(targetText)\(rateText) — \(etaText).")
    }

    private func longerTermGoals(state: [String: Any], highestBoss: Int) -> [String] {
        var goals: [String] = []
        let difficulty = number(state, "difficulty")
        let nextTitan = state["nextTitanName"] as? String ?? "next Titan"
        if highestBoss >= 58 || difficulty > 0 {
            goals.append("Defeat \(nextTitan); prioritize its spawn window, progression drop, and set completion over routine farming.")
        }

        // The injected planner emits a source-backed event graph.  Prefer its live
        // unresolved gates over generic prose so the roadmap changes at the exact
        // clue, boss, set, unlock, and difficulty transition the game is on.
        if let nodes = state["goalNodes"] as? [[String: Any]] {
            let familyRank = ["puzzle": 0, "boss": 1, "progression": 2, "challenge": 3]
            let candidates = nodes.filter { node in
                let family = node["family"] as? String ?? ""
                return familyRank[family] != nil
            }.sorted { left, right in
                let lf = familyRank[left["family"] as? String ?? ""] ?? 99
                let rf = familyRank[right["family"] as? String ?? ""] ?? 99
                if lf != rf { return lf < rf }
                let le = (left["etaSeconds"] as? NSNumber)?.intValue ?? -1
                let re = (right["etaSeconds"] as? NSNumber)?.intValue ?? -1
                if (le >= 0) != (re >= 0) { return le >= 0 }
                return le >= 0 && re >= 0 ? le < re : false
            }
            for node in candidates {
                let label = node["label"] as? String ?? ""
                guard !label.isEmpty && !goals.contains(where: { $0.contains(label) }) else { continue }
                let eta = (node["etaSeconds"] as? NSNumber)?.intValue ?? -1
                goals.append(eta >= 0 ? "\(label) — ETA \(formatEstimate(eta))." : label + ".")
                if goals.count >= 6 { return goals }
            }
        }

        if difficulty == 0 {
            if highestBoss < 17 { goals.append("Defeat Boss 17; unlock Augments and custom EXP purchases.") }
            if highestBoss < 30 { goals.append("Defeat Boss 30; unlock the Time Machine and establish the gold-growth loop.") }
            if highestBoss < 37 { goals.append("Defeat Boss 37; unlock Magic and Blood Magic.") }
            if !(state["nguUnlocked"] as? Bool ?? false) { goals.append("Complete the Number set and unlock NGUs; shift spare resources into permanent growth.") }
            if highestBoss >= 58 { goals.append("Clear the highest-return unlocked Challenge completion by permanent reward per expected minute.") }
            if highestBoss >= 301 { goals.append("Finish The Beast v4 and the 10,000% rich-stat requirement; enter Evil.") }
        } else if difficulty == 1 {
            if !(state["hacksUnlocked"] as? Bool ?? false) { goals.append("Unlock Resource 3 and Hacks; begin milestone-efficient permanent growth.") }
            if !(state["wishesUnlocked"] as? Bool ?? false) { goals.append("Unlock Wishes and fund the highest gate-opening wish slots.") }
            if highestBoss < 301 { goals.append("Advance Evil Fight Bosses toward Boss 301.") }
            else { goals.append("Defeat Exile v4 and enter Sadistic.") }
        } else {
            if !(state["cardsUnlocked"] as? Bool ?? false) { goals.append("Unlock Cards, Mayo generation, and tagging.") }
            goals.append("Advance the limiting Wish, Hack, NGU, MacGuffin, PP, or QP milestone by measured permanent gain per minute.")
        }

        if goals.count < 4 { goals.append("Finish the highest-value reachable Adventure set and merge its confirmed upgrades.") }
        return Array(goals.prefix(6))
    }

    private func number(_ object: [String: Any], _ key: String) -> Int {
        return (object[key] as? NSNumber)?.intValue ?? 0
    }

    private func expPurchaseName(_ decision: String) -> String {
        let prefixes = ["Saving briefly for ", "Saving EXP for ", "Saving for ", "Held for ", "Buying ",
                        "No spendable EXP above the "]
        for prefix in prefixes {
            guard let range = decision.range(of: prefix, options: .caseInsensitive) else { continue }
            var label = String(decision[range.upperBound...])
            for stop in [":", ";", " now", " on this decision cycle", " because ", " toward ", " at Boss "] {
                if let stopRange = label.range(of: stop, options: .caseInsensitive),
                   stopRange.lowerBound > label.startIndex {
                    label = String(label[..<stopRange.lowerBound])
                }
            }
            label = label.trimmingCharacters(in: .whitespacesAndNewlines)
                .trimmingCharacters(in: CharacterSet(charactersIn: "."))
            if label.lowercased().hasPrefix("the marginally best ") {
                label = String(label.dropFirst("the marginally best ".count))
            }
            if !label.isEmpty { return label }
        }
        let lower = decision.lowercased()
        if lower.contains("adventure-stat atoms") { return "Adventure-stat purchases" }
        if lower.contains("adventure atom") { return "next-zone Adventure stat" }
        if lower.contains("energy-speed") { return "Energy speed" }
        if lower.contains("energy packages") { return "Boss 17 Energy packages" }
        return "next validated EXP purchase"
    }

    private func numberDouble(_ object: [String: Any], _ key: String) -> Double {
        return (object[key] as? NSNumber)?.doubleValue ?? 0
    }

    private func formatEstimate(_ seconds: Int) -> String {
        if seconds < 0 { return "not currently forecastable; recalculated live" }
        if seconds == 0 { return "now" }
        return "about " + formatDuration(seconds)
    }

    private func formatDuration(_ seconds: Int) -> String {
        let value = max(0, seconds)
        if value < 60 { return "\(value)s" }
        if value < 3600 { return "\(value / 60)m \(value % 60)s" }
        return "\(value / 3600)h \((value % 3600) / 60)m"
    }

    private func formatExactDuration(_ seconds: Int) -> String {
        let value = max(0, seconds)
        return "\(value / 3600)h \((value % 3600) / 60)m \(value % 60)s (\(groupedInteger(value)) seconds)"
    }

    private func groupedInteger(_ value: Int) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.maximumFractionDigits = 0
        return formatter.string(from: NSNumber(value: value)) ?? "\(value)"
    }

    private func wallClockEstimate(_ seconds: Int) -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "h:mm:ss a"
        return formatter.string(from: Date().addingTimeInterval(TimeInterval(max(0, seconds))))
    }

    private func shortNumber(_ value: Double) -> String {
        if value >= 1_000_000_000 { return String(format: "%.2fB", value / 1_000_000_000) }
        if value >= 1_000_000 { return String(format: "%.2fM", value / 1_000_000) }
        if value >= 1_000 { return String(format: "%.2fK", value / 1_000) }
        return String(format: "%.1f", value)
    }

    func windowWillClose(_ notification: Notification) {
        timer?.invalidate()
        NSApp.terminate(nil)
    }
}

let arguments = CommandLine.arguments
guard arguments.count >= 3 else {
    fputs("Usage: ngu-action-monitor <actions.log> <decision.json>\n", stderr)
    exit(2)
}
let app = NSApplication.shared
let delegate = ActionMonitor(logPath: arguments[1], decisionPath: arguments[2])
app.delegate = delegate
app.run()
