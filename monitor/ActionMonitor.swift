/*
FILE PURPOSE

ActionMonitor is a separate read-only macOS AppKit process. It tails confirmed actions and
schema-validated decision.json, requires its matching deployment.json identity, rejects
stale/build/PID/session/out-of-order telemetry, and admits actions only after the exact durable
session marker. It renders the decision/root epoch, staged authority, scheduler shadow statistics,
current/next rebirth policy, finite and unavailable challenge/difficulty/END ETAs, capacity,
transaction states, collection debt, and a sparse Key Events history. It has no game
handle or mutation path; display features must follow explicit truthful producer fields and never
turn a missing estimate into a zero-second countdown. The Live Actions presentation is the visual
baseline and should not be restyled by goal/event changes.
*/
import AppKit

final class ActionMonitor: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private let logPath: String
    private let decisionPath: String
    private let deploymentPath: String
    private let launchedAt = Date()
    private var offset: UInt64 = 0
    private var producerPid: Int?
    private var buildId: String?
    private var producerSessionId: String?
    private var lastDecisionSequence = 0
    private var lastRenderedSequence = -1
    private var lastAcceptedModification = Date.distantPast
    private var producerEpoch = 0
    private var window: NSWindow!
    private var textView: NSTextView!
    private var goalsTextView: NSTextView!
    private var keyEventsTextView: NSTextView!
    private var keyEventRemainder = ""
    private var actionLineRemainder = ""
    private var actionSessionId: String?
    private var actionSessionAdmitted = false
    private var statusLabel: NSTextField!
    private var summaryLabel: NSTextField!
    private var timer: Timer?
    private let logLineRegex = try! NSRegularExpression(
        pattern: #"^(\d{2}:\d{2}:\d{2}\.\d{3}) (\[[^\]]+\]) (\([^\)]+\)) (.*)$"#)

    init(logPath: String, decisionPath: String) {
        self.logPath = logPath
        self.decisionPath = decisionPath
        self.deploymentPath = URL(fileURLWithPath: decisionPath)
            .deletingLastPathComponent().appendingPathComponent("deployment.json").path
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

        let keyEventsTab = NSTabViewItem(identifier: "key-events")
        keyEventsTab.label = "Key Events"
        let keyEventsScroll = makeScrollView(frame: tabs.contentRect)
        keyEventsTextView = makeTextView(frame: keyEventsScroll.bounds)
        keyEventsTextView.font = NSFont.monospacedSystemFont(ofSize: 12.5, weight: .regular)
        keyEventsTextView.textStorage?.setAttributedString(keyEventsHeader())
        keyEventsScroll.documentView = keyEventsTextView
        keyEventsTab.view = keyEventsScroll
        tabs.addTabViewItem(keyEventsTab)

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
        if let data = try? Data(contentsOf: URL(fileURLWithPath: decisionPath)),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let stage = object["stage"] as? String {
            let schema = number(object, "schemaVersion")
            let incomingPid = number(object, "producerPid")
            let incomingBuild = object["buildId"] as? String ?? ""
            let incomingSession = object["producerSessionId"] as? String ?? ""
            let sequence = number(object, "decisionSequence")
            let attributes = try? FileManager.default.attributesOfItem(atPath: decisionPath)
            let modified = attributes?[.modificationDate] as? Date ?? .distantPast
            let age = max(0, Date().timeIntervalSince(modified))
            let gameEpoch = object["gameEpochFingerprint"] as? String ?? ""
            if schema != 2 || incomingPid <= 0 || incomingBuild.isEmpty
                || incomingSession.isEmpty || sequence <= 0 || gameEpoch.isEmpty {
                statusLabel.stringValue = "AUTOMATION • REJECTED UNVERIFIED OR OUT-OF-SEQUENCE TELEMETRY"
                statusLabel.textColor = .systemRed
                return
            }
            if !deploymentMatchesDecision(object) {
                invalidateActionTail()
                statusLabel.stringValue = "AUTOMATION • REJECTED DEPLOYMENT / DECISION SESSION MISMATCH"
                statusLabel.textColor = .systemRed
                summaryLabel.stringValue = "No actions are admitted until deployment.json and decision.json share PID, session, build, and artifact hashes."
                summaryLabel.textColor = .systemRed
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
                && (producerPid != incomingPid || buildId != incomingBuild
                    || producerSessionId != incomingSession)
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
            if actionSessionId != incomingSession {
                resetActionTail(for: incomingSession)
            }
            producerPid = incomingPid
            buildId = incomingBuild
            producerSessionId = incomingSession
            lastDecisionSequence = sequence
            if modified > lastAcceptedModification { lastAcceptedModification = modified }
            let elapsed = number(object, "rebirthElapsed")
            let synced = object["synced"] as? Bool ?? false
            let enabled = object["enabled"] as? Bool ?? false
            let mutationsEnabled = object["mutationsEnabled"] as? Bool ?? false
            let transactionComplete = object["automationTransactionComplete"] as? Bool ?? false
            let transactionError = object["automationTransactionError"] as? String ?? ""
            let transactionState = rootTransactionState(object)
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
            } else if transactionState == "Quarantined" {
                statusLabel.stringValue = "\(mode) • QUARANTINED ROOT • SNAPSHOT #\(sequence)"
                statusLabel.textColor = .systemRed
                summaryLabel.stringValue = transactionError.isEmpty
                    ? "The root epoch or a child postcondition was quarantined; no success is inferred."
                    : "Quarantined root: \(transactionError)"
                summaryLabel.textColor = .systemRed
            } else if transactionState == "Error" {
                statusLabel.stringValue = "\(mode) • ROOT ERROR • SNAPSHOT #\(sequence)"
                statusLabel.textColor = .systemRed
                summaryLabel.stringValue = transactionError.isEmpty
                    ? "The latest root failed without a supplied error detail."
                    : "Root error: \(transactionError)"
                summaryLabel.textColor = .systemRed
            } else if !transactionComplete || transactionState == "Pending" {
                statusLabel.stringValue = "\(mode) • PENDING AUTOMATION ROOT • SNAPSHOT #\(sequence)"
                statusLabel.textColor = .systemOrange
                summaryLabel.stringValue = transactionError.isEmpty
                    ? "A root or child intent is pending; this is not a committed cycle."
                    : "Pending cycle: \(transactionError)"
                summaryLabel.textColor = .systemOrange
            } else if transactionState == "Held" {
                statusLabel.stringValue = "\(mode) • AUTOMATION ROOT HELD • SNAPSHOT #\(sequence)"
                statusLabel.textColor = .systemOrange
                summaryLabel.stringValue = "No nonzero mutation root was admitted for this decision frame."
                summaryLabel.textColor = .systemOrange
            } else {
                let target = number(object, "rebirthSeconds")
                let remaining = max(0, target - elapsed)
                let executionHold = object["rebirthExecutionHold"] as? Bool ?? false
                statusLabel.stringValue = target < 0
                    ? "NO RESET • ACTIVE CHALLENGE POLICY"
                    : executionHold
                    ? "REBIRTH HOLD • NO EXECUTABLE RESET SCHEDULED"
                    : "REBIRTH \(formatExactDuration(remaining))"
                statusLabel.textColor = target < 0 || executionHold ? .systemOrange : .systemGreen
                updateSummary(object)
            }
            if sequence != lastRenderedSequence {
                lastRenderedSequence = sequence
                renderGoals(object)
            }
            tailSessionActions()
        } else {
            statusLabel.stringValue = "AUTOMATION • WAITING FOR BOT"
            statusLabel.textColor = .systemOrange
        }
    }

    private func deploymentMatchesDecision(_ state: [String: Any]) -> Bool {
        guard let data = try? Data(contentsOf: URL(fileURLWithPath: deploymentPath)),
              let deployment = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return false }
        let schema = number(deployment, "schemaVersion")
        let session = state["producerSessionId"] as? String ?? ""
        let build = state["buildId"] as? String ?? ""
        let disk = state["diskArtifactSha256"] as? String ?? ""
        let game = state["gameAssemblySha256"] as? String ?? ""
        return schema >= 2
            && number(deployment, "producerPid") == number(state, "producerPid")
            && (deployment["producerSessionId"] as? String ?? "") == session
            && (deployment["activeBuildId"] as? String ?? "").caseInsensitiveCompare(build) == .orderedSame
            && !disk.isEmpty
            && (deployment["diskArtifactSha256"] as? String ?? "").caseInsensitiveCompare(disk) == .orderedSame
            && !game.isEmpty
            && (deployment["gameAssemblySha256"] as? String ?? "").caseInsensitiveCompare(game) == .orderedSame
    }

    private func resetActionTail(for sessionId: String) {
        actionSessionId = sessionId
        actionSessionAdmitted = false
        actionLineRemainder = ""
        keyEventRemainder = ""
        offset = 0
        textView.textStorage?.setAttributedString(NSAttributedString())
        keyEventsTextView.textStorage?.setAttributedString(keyEventsHeader())
    }

    private func invalidateActionTail() {
        actionSessionId = nil
        actionSessionAdmitted = false
        actionLineRemainder = ""
        keyEventRemainder = ""
        offset = 0
        textView.textStorage?.setAttributedString(NSAttributedString())
        keyEventsTextView.textStorage?.setAttributedString(keyEventsHeader())
    }

    private func sessionBoundChunk(_ chunk: String) -> String {
        guard let sessionId = actionSessionId, !sessionId.isEmpty else { return "" }
        let combined = actionLineRemainder + chunk
        var lines = combined.components(separatedBy: "\n")
        if combined.hasSuffix("\n") {
            actionLineRemainder = ""
            if lines.last?.isEmpty == true { lines.removeLast() }
        } else {
            actionLineRemainder = lines.popLast() ?? ""
        }
        var selected: [String] = []
        for line in lines {
            if line.hasPrefix("=== SESSION ") && line.hasSuffix(" ===") {
                actionSessionAdmitted = line.contains(" id \(sessionId) build ")
                continue
            }
            /*
            HOLD is decision/safety evidence, not an action the bot performed.  Its exact reason
            remains available in decision telemetry and the durable actions.log for diagnosis,
            but admitting it into the operator's Live Actions feed made unavailable bosses look
            like attempted work.  Failed/rejected actions remain visible; only clean no-op holds
            are omitted from the presentation stream.
            */
            if actionSessionAdmitted && !line.contains(" [HOLD] ") {
                selected.append(line)
            }
        }
        return selected.isEmpty ? "" : selected.joined(separator: "\n") + "\n"
    }

    private func tailSessionActions() {
        guard actionSessionId != nil else { return }
        let fm = FileManager.default
        guard let attrs = try? fm.attributesOfItem(atPath: logPath),
              let size = attrs[.size] as? NSNumber else { return }
        let length = size.uint64Value
        if length < offset {
            offset = 0
            actionSessionAdmitted = false
            actionLineRemainder = ""
        }
        guard length > offset, let handle = FileHandle(forReadingAtPath: logPath) else { return }
        defer { try? handle.close() }
        do {
            try handle.seek(toOffset: offset)
            let data = handle.readDataToEndOfFile()
            offset += UInt64(data.count)
            guard let raw = String(data: data, encoding: .utf8), !raw.isEmpty else { return }
            let chunk = sessionBoundChunk(raw)
            guard !chunk.isEmpty else { return }
            textView.textStorage?.append(coloredLog(chunk))
            if let storage = textView.textStorage, storage.length > 750_000 {
                storage.deleteCharacters(in: NSRange(location: 0, length: min(150_000, storage.length)))
            }
            textView.scrollToEndOfDocument(nil)
            let keyChunk = keyEventChunk(chunk)
            if !keyChunk.isEmpty {
                keyEventsTextView.textStorage?.append(coloredLog(keyChunk))
                if let storage = keyEventsTextView.textStorage, storage.length > 500_000 {
                    let headerLength = keyEventsHeader().length
                    let removable = max(0, min(100_000, storage.length - headerLength))
                    if removable > 0 {
                        storage.deleteCharacters(in: NSRange(location: headerLength, length: removable))
                    }
                }
                keyEventsTextView.scrollToEndOfDocument(nil)
            }
        } catch { }
    }

    private func rootTransactionState(_ state: [String: Any]) -> String {
        let root = state["mutationRoot"] as? [String: Any] ?? [:]
        let rootId = number(root, "id")
        let rootState = (root["state"] as? String ?? "").lowercased()
        let decisionEpoch = state["gameEpochFingerprint"] as? String ?? ""
        let rootEpoch = root["epochFingerprint"] as? String ?? ""
        if number(root, "quarantinedSteps") > 0 || rootState.contains("quarant")
            || !decisionEpoch.isEmpty && !rootEpoch.isEmpty && decisionEpoch != rootEpoch {
            return "Quarantined"
        }
        if !(state["automationTransactionError"] as? String ?? "").isEmpty { return "Error" }
        if number(root, "pendingSteps") > 0 || rootState == "open" || rootState == "pending" {
            return "Pending"
        }
        if rootId <= 0 || rootState.isEmpty || rootState == "not-planned" || rootState == "held" {
            return "Held"
        }
        return (state["automationTransactionComplete"] as? Bool ?? false)
            ? "Committed" : "Pending"
    }

    private func updateSummary(_ state: [String: Any]) {
        guard state["synced"] as? Bool ?? false else {
            summaryLabel.stringValue = "SAFE PAUSE  •  no game mutations until active gameplay is verified"
            summaryLabel.textColor = .systemOrange
            return
        }
        let selectedBoss = number(state, "bossSelectedId")
        let bossEta = number(state, "bossDefeatEtaSeconds")
        let bossEtaHorizon = state["bossEtaProjectionHorizonSeconds"] == nil
            ? 604800 : max(1, number(state, "bossEtaProjectionHorizonSeconds"))
        let zone = state["adventureTargetName"] as? String ?? "selecting zone"
        let rebirthTarget = number(state, "rebirthSeconds")
        let rebirthElapsed = number(state, "rebirthElapsed")
        let rebirthRemaining = max(0, rebirthTarget - rebirthElapsed)
        let noResetPolicy = rebirthTarget < 0
        let rebirthExecutionHold = state["rebirthExecutionHold"] as? Bool ?? false
        // Number loss and a slower reset/replay route are optimizer costs, not native
        // mutation prohibitions. Only explicit execution authority or a planner hold
        // may turn the status line into a route hold.
        let rebirthBlocked = !(state["rebirthExecutionEnabled"] as? Bool ?? true)
        let rebirthText = noResetPolicy ? "no reset — active challenge"
            : rebirthExecutionHold ? "hold — recalculating"
            : rebirthRemaining > 0 ? formatExactDuration(rebirthRemaining)
            : rebirthBlocked ? "route hold" : "now"
        let bossText = bossEta < 0 ? "beyond " + formatEstimate(bossEtaHorizon) + " model"
            : "in " + formatEstimate(bossEta)
        statusLabel.stringValue = "REBIRTH \(rebirthText)   •   BOSS \(selectedBoss) \(bossText)"
        statusLabel.textColor = rebirthTarget < 0 || rebirthExecutionHold
            || rebirthBlocked && rebirthRemaining <= 0 ? .systemOrange : .systemGreen

        let exp = numberDouble(state, "exp")
        let expTarget = numberDouble(state, "expTargetCost")
        let expShortfall = numberDouble(state, "expShortfall")
        let explicitExpTargetName = (state["expTargetName"] as? String ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let expTargetName = explicitExpTargetName.isEmpty
            ? expPurchaseName(state["expDecision"] as? String ?? "")
            : explicitExpTargetName
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
        let bossEtaHorizon = state["bossEtaProjectionHorizonSeconds"] == nil
            ? 604800 : max(1, number(state, "bossEtaProjectionHorizonSeconds"))
        let bossFitsRebirth = state["bossDefeatFitsRebirthHorizon"] as? Bool ?? (bossViabilityETA >= 0)
        let bossRebirthSlack = number(state, "bossRebirthSlackSeconds")
        let bossViabilityReason = state["bossViabilityReason"] as? String ?? "waiting for the next exact combat viability result"
        let trainingGoal = state["trainingGoal"] as? String ?? "Speed-cap unlocked Basic Trainings"
        let trainingETA = number(state, "trainingEtaSeconds")
        let rebirthTarget = number(state, "rebirthSeconds")
        let rebirthElapsed = number(state, "rebirthElapsed")
        let rebirthRemaining = max(0, rebirthTarget - rebirthElapsed)
        let noResetPolicy = rebirthTarget < 0
        let rebirthExecutionHold = state["rebirthExecutionHold"] as? Bool ?? false
        let rebirthReason = state["rebirthReason"] as? String ?? "current highest-value checkpoint"
        let rebirthNextPositiveETA = number(state, "rebirthNextPositiveEtaSeconds")
        let rebirthNextEvaluationETA = number(state, "rebirthNextEvaluationEtaSeconds")
        let rebirthETAReason = state["rebirthEtaReason"] as? String ?? ""
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
        let rebirthRecoveryResetETA = number(state, "rebirthRecoveryResetRouteEtaSeconds")
        let rebirthRecoveryContinueETA = number(state, "rebirthRecoveryContinueRouteEtaSeconds")
        let rebirthOptimizerRecoveryETA = number(state, "rebirthOptimizerRecordRecoveryEtaSeconds")
        let rebirthRecoveryRemainingBosses = number(state, "rebirthRecoveryRemainingBosses")
        let rebirthRecoveryReason = state["rebirthRecoveryReason"] as? String ?? "recovery route calculation pending"
        let rebirthSafetyBlockReason = state["rebirthSafetyBlockReason"] as? String ?? ""
        let challengeEvidence = state["challengeEvidenceSummary"] as? String
            ?? "No challenge-admission evidence was emitted."
        let transactionError = state["automationTransactionError"] as? String ?? ""
        let transactionState = rootTransactionState(state)
        let mutationRoot = state["mutationRoot"] as? [String: Any] ?? [:]
        let rootId = optionalNonnegativeInt(mutationRoot, "id").flatMap { $0 > 0 ? $0 : nil }
        let rootNativeState = mutationRoot["state"] as? String ?? "Unavailable"
        let decisionEpoch = state["gameEpochFingerprint"] as? String ?? ""
        let rootEpoch = mutationRoot["epochFingerprint"] as? String ?? ""
        let rootEpochMatch = !decisionEpoch.isEmpty && !rootEpoch.isEmpty
            ? decisionEpoch == rootEpoch : nil
        let rootCounts = [
            countLabel(mutationRoot, "committedSteps", "committed"),
            countLabel(mutationRoot, "heldSteps", "held"),
            countLabel(mutationRoot, "pendingSteps", "pending"),
            countLabel(mutationRoot, "rejectedSteps", "rejected"),
            countLabel(mutationRoot, "quarantinedSteps", "quarantined")
        ].compactMap { $0 }.joined(separator: " · ")
        let authorityStage = state["authorityStage"] as? String ?? "Unavailable"
        let authoritySummary = stagedAuthoritySummary(state)
        let scheduler = state["globalScheduler"] as? [String: Any] ?? [:]
        let schedulerStatus = scheduler["status"] as? String ?? "Unavailable"
        let schedulerAuthority = scheduler["authority"] as? String ?? "Unavailable"
        let schedulerAction = nonempty(scheduler["action"] as? String) ?? "Unavailable"
        let schedulerActionId = nonempty(scheduler["actionId"] as? String)
        let schedulerEvent = nonempty(scheduler["nextEvent"] as? String) ?? "Unavailable"
        let schedulerEventId = nonempty(scheduler["eventId"] as? String)
        let schedulerProvenance = nonempty(scheduler["provenance"] as? String)
        let schedulerSamples = optionalNonnegativeInt(scheduler, "sampleCount")
        let schedulerConfidence = optionalUnitDouble(scheduler, "confidence")
        let schedulerEvidence = evidenceLabel(
            provenance: schedulerProvenance, samples: schedulerSamples,
            confidence: schedulerConfidence)
        let schedulerBlocker = nonempty(scheduler["blocker"] as? String)
        let schedulerBlockerDetail = nonempty(scheduler["blockerDetail"] as? String)
        let schedulerHashes = ["snapshotHash", "modelHash", "objectiveHash"].map {
            shortHash(scheduler[$0] as? String)
        }.joined(separator: " / ")
        let schedulerStats = "mean \(availableEstimate(optionalNonnegativeDouble(scheduler, "meanSeconds"))), p50 \(availableEstimate(optionalNonnegativeDouble(scheduler, "p50Seconds"))), p90 \(availableEstimate(optionalNonnegativeDouble(scheduler, "p90Seconds"))); lower \(availableEstimate(optionalNonnegativeDouble(scheduler, "lowerBoundSeconds"))), gap \(availableEstimate(optionalNonnegativeDouble(scheduler, "gapSeconds"))), regret \(availableEstimate(optionalNonnegativeDouble(scheduler, "regretSeconds")))"
        let difficultyValue = optionalNonnegativeInt(state, "difficulty")
        let difficultyCurrent = difficultyName(difficultyValue)
        let difficultyTarget = firstNonempty(state,
            ["difficultyTarget", "nextDifficulty", "difficultyTransitionTarget"])
        let difficultyETA = firstOptionalSeconds(state,
            ["difficultyEtaSeconds", "difficultyTransitionEtaSeconds"])
        let difficultyBlocker = firstNonempty(state,
            ["difficultyBlocker", "difficultyTransitionReason"])
        let challengeClearETA = firstOptionalSeconds(state,
            ["nextChallengeEtaSeconds", "challengeEtaSeconds", "challengePessimisticClearSeconds"])
        let challengeRecoveryETA = firstOptionalSeconds(state, ["challengeRecoveryEtaSeconds"])
        let endObjective = nonempty(state["endgameObjective"] as? String) ?? "Unavailable"
        let endMissing = nonempty(state["endgameMissingSummary"] as? String) ?? "Unavailable"
        let endReady = state["endgameReadyToTrigger"] as? Bool
        let endAuthorized = state["endgameExecutionAuthorized"] as? Bool
        let endState = endReady == true && endAuthorized == true ? "Pending"
            : endReady == false || endAuthorized == false ? "Held" : "Unavailable"
        let producerPid = number(state, "producerPid")
        let producerSession = state["producerSessionId"] as? String ?? "unavailable"
        let activeBuild = state["buildId"] as? String ?? "unavailable"
        let diskHash = state["diskArtifactSha256"] as? String ?? "unavailable"
        let gameHash = state["gameAssemblySha256"] as? String ?? "unavailable"
        let activeMatchesDisk = state["activeMatchesDisk"] as? String ?? "unknown"
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
        let majorUnlockActive = state["majorUnlockActive"] as? Bool ?? false
        let majorUnlockName = state["majorUnlockName"] as? String ?? "major mechanic"
        let majorUnlockGoal = state["majorUnlockGoal"] as? String ?? "complete the verified unlock condition"
        let majorUnlockReason = state["majorUnlockReason"] as? String ?? ""
        let majorUnlockGuaranteed = state["majorUnlockGuaranteedDrop"] as? Bool ?? false
        let majorUnlockChance = numberDouble(state, "majorUnlockDropChance")
        let energyCurrent = numberDouble(state, "energyCurrent")
        let energyIdle = numberDouble(state, "energyIdle")
        let energyUtilization = numberDouble(state, "energyUtilization")
        let energyIdleReason = state["energyIdleReason"] as? String ?? "waiting-for-telemetry"
        let energyIncome = numberDouble(state, "energyIncomePerSecond")
        let basicTrainingEnergy = numberDouble(state, "energyBasicTrainingAllocated")
        let nonBasicTrainingEnergy = numberDouble(state, "energyNonBasicTrainingAllocated")
        let loadoutDecision = state["loadoutDecision"] as? String ?? "Evaluating owned equipment"
        let boostDecision = state["boostDecision"] as? String ?? "Evaluating future equipment boost humps"
        let trashDecision = state["trashDecision"] as? String ?? "Conservative trash audit pending"
        let filterDecision = state["filterDecision"] as? String ?? "Collection-safe loot-filter audit pending"
        let collectionBackfill = state["collectionIsBackfill"] as? Bool ?? false
        let collectionRemaining = number(state, "collectionRemainingItems")
        let collectionProjectedSlots = number(state, "collectionProjectedNewSlots")
        let collectionReserve = number(state, "collectionRequiredFreeReserve")
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
        let advancedTrainingHorizon = state["advancedTrainingHorizonDecision"] as? String
            ?? "Advanced Training next-zone value is being evaluated"
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
        } else if majorUnlockActive && number(state, "adventureZone") == -1 {
            let eta = recoveryETA > 0 ? " — ETA \(formatEstimate(recoveryETA))" : ""
            adventureStatus = "MAJOR UNLOCK — \(majorUnlockName): recovering/pre-casting in Safe Zone for \(majorUnlockGoal) (HP \(shortNumber(currentHP))/\(shortNumber(maxHP)))\(eta)."
        } else if majorUnlockActive {
            let drop = majorUnlockGuaranteed ? "the first qualifying kill is guaranteed"
                : majorUnlockChance > 0 ? "per-kill drop chance \(String(format: "%.2f", 100 * majorUnlockChance))%" : "native unlock fight"
            adventureStatus = "MAJOR UNLOCK — \(majorUnlockName): \(majorUnlockGoal) in \(zone), ACTIVE fast-manual with between-fight recovery; \(drop). \(majorUnlockReason)"
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
        if majorUnlockActive {
            shortTerm.append("Unlock \(majorUnlockName): \(majorUnlockGoal).")
        }
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
                bossEtaText = "no finite defeat ETA within the bounded (formatEstimate(bossEtaHorizon)) current-allocation model"
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
            shortTerm.append("Protect loot capacity: only \(inventoryFree)/\(inventoryTotal) slots are free versus a \(collectionReserve)-slot live reserve; AP space is promoted ahead of larger permanent purchases.")
        }
        if noResetPolicy {
            shortTerm.append("Continue the active no-reset challenge policy; no ordinary rebirth is scheduled.")
        } else if rebirthExecutionHold {
            shortTerm.append("Rebirth is unscheduled: continuously re-evaluate until the event model admits a valid mutation boundary.")
        } else if !rebirthExecutionEnabled {
            let reason = rebirthSafetyBlockReason.isEmpty
                ? "rebirth execution is disabled" : rebirthSafetyBlockReason
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
        if let expQolPolicy = state["expQolPolicy"] as? String, !expQolPolicy.isEmpty {
            resourceDecisions.append("EXP QoL: \(expQolPolicy).")
        }
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
            let economics = rebirthPreviewMonotonic ? "NON-DECREASING NUMBER"
                : "PRICED NUMBER LOSS — NOT AN EXECUTION PROHIBITION"
            rewardForecast = "Current native preview ratios: Attack ×\(String(format: "%.4g", attackGain)), Defense ×\(String(format: "%.4g", defenseGain)) [\(economics)]; selected-checkpoint worst ratio ×\(String(format: "%.4g", numberDouble(state, "rebirthMinimumNumberRatio"))); selected checkpoint yields \(number(state, "rebirthProjectedAp")) time-based AP before Titan bonuses."
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
            SELECTED REPEATED-CYCLE POLICY ETA: \(rebirthOptimizerRecoveryETA >= 0 ? formatEstimate(rebirthOptimizerRecoveryETA) : "outside the bounded recovery model")
            RESET + REPLAY ROUTE: \(resetEtaText)
            CONTINUE CURRENT RUN ROUTE: \(continueEtaText)
            ROUTE VERDICT: \(rebirthRecoveryReason)
            """
        } else {
            recoveryForecast = "RECORD RECOVERY MODE: complete; ordinary long-run growth objective applies."
        }

        let nextPositiveText = state["rebirthNextPositiveEtaSeconds"] != nil
            && rebirthNextPositiveETA >= 0 ? formatEstimate(rebirthNextPositiveETA) : "no finite candidate emitted"
        let nextEvaluationText = state["rebirthNextEvaluationEtaSeconds"] != nil
            && rebirthNextEvaluationETA >= 0 ? formatEstimate(rebirthNextEvaluationETA) : "next live control tick"
        let rebirthPolicy = noResetPolicy ? "NO RESET — active challenge forbids rebirth"
            : rebirthExecutionHold ? "HOLD — no executable reset is scheduled"
            : !rebirthExecutionEnabled ? "DISABLED — rebirth execution is off"
            : rebirthRemaining <= 0 ? "RESET DUE — verify the native boundary"
            : "RESET at the selected checkpoint"
        let challengeAdmitted = challengeEvidence.range(
            of: #"^[A-Z0-9]+-\d+(?:\s+\[|:\s+target Boss)"#,
            options: .regularExpression
        ) != nil
        let challengeGlance = challengeAdmitted
            ? challengeEvidence.components(separatedBy: " | ").first ?? challengeEvidence
            : "NONE ADMITTED — \(challengeEvidence)"
        let transactionGlance = transactionError.isEmpty
            ? transactionState : "\(transactionState.uppercased()) — \(transactionError)"
        let shortBuild = String(activeBuild.prefix(12))
        let shortSession = String(producerSession.prefix(12))
        let shortDisk = String(diskHash.prefix(12))
        let shortGame = String(gameHash.prefix(12))

        let roundExplanation = rebirthReason.lowercased().contains("exact")
            ? "WHY THE ROUND NUMBER: this second is a discontinuity in NGU Idle's native Number time-multiplier formula; it was evaluated, not assumed."
            : "WHY THIS SECOND: it is the highest-scoring live event or integer-second candidate in the current finite-horizon model."
        let bossGlance = bossReady ? "READY NOW"
            : bossViabilityETA < 0 ? "NO FINITE ETA ≤ " + formatEstimate(bossEtaHorizon)
            : "ETA " + formatEstimate(bossViabilityETA)

        let body = """
        AT A GLANCE
        ▶ NEXT: \(shortTerm.first ?? "Re-evaluating the next verified action.")
        ◆ BOSS: selected \(selectedBoss) / next record \(nextBoss) — \(bossGlance)
        ◆ ADVENTURE: \(zone) — \(majorUnlockActive ? "MAJOR UNLOCK: " + majorUnlockName.uppercased() : collectionBackfill ? "MAXX BACKFILL" : "FORWARD COLLECTION")
        ◆ INVENTORY: \(inventoryUsed)/\(inventoryTotal) used, \(inventoryFree) free — \(inventoryPressure) PRESSURE
        ◆ REBIRTH: \(rebirthPolicy)\(noResetPolicy || rebirthExecutionHold ? "" : " — " + formatExactDuration(rebirthRemaining) + " remaining")
        ◆ CHALLENGE: \(challengeGlance)
        ◆ TRANSACTION: \(transactionGlance)

        EXECUTION ENVELOPE — DEPLOYMENT + DECISION EPOCH
        JOIN: BOUND — deployment PID/session/build/artifact hashes match this decision frame
        DECISION EPOCH: \(shortHash(decisionEpoch))
        ROOT: \(rootId.map { "#\($0)" } ?? "Unavailable") · \(rootNativeState) · epoch \(shortHash(rootEpoch)) (\(rootEpochMatch == true ? "matched" : rootEpochMatch == false ? "MISMATCH / QUARANTINED" : "unavailable"))
        ROOT COUNTS: \(rootCounts.isEmpty ? "Unavailable" : rootCounts)
        SESSION ACTION TAIL: bound only to \(shortSession); older and later session blocks are excluded
        CAPACITY: \(capacitySummary(state))
        AUTHORITY STAGE: \(authorityStage)
        STAGED ROUTES: \(authoritySummary)

        GLOBAL SCHEDULER — SHADOW ONLY
        STATE: \(schedulerStatus) · AUTHORITY: \(schedulerAuthority) · can execute \((scheduler["canExecute"] as? Bool).map { $0 ? "yes" : "no" } ?? "Unavailable")
        ACTION: \(schedulerAction)\(schedulerActionId.map { " · \($0)" } ?? "")
        NEXT EVENT: \(schedulerEvent)\(schedulerEventId.map { " · \($0)" } ?? "")
        HASHES S / M / O: \(schedulerHashes)
        TERMINAL STATISTICS: \(schedulerStats)
        EVIDENCE: \(schedulerEvidence)
        BLOCKER: \(schedulerBlocker.map { $0 + (schedulerBlockerDetail.map { " — " + $0 } ?? "") } ?? "Unavailable")

        PROGRESSION HORIZONS
        REBIRTH: \(rebirthPolicy) — \(noResetPolicy || rebirthExecutionHold ? "ETA unavailable while held" : availableEstimate(Double(rebirthRemaining)))
        CHALLENGE: \(challengeGlance) — clear \(availableEstimate(challengeClearETA.map(Double.init))), recovery \(availableEstimate(challengeRecoveryETA.map(Double.init)))
        DIFFICULTY: \(difficultyCurrent)\(difficultyTarget.map { " → " + $0 } ?? "") — \(availableEstimate(difficultyETA.map(Double.init)))\(difficultyBlocker.map { " — " + $0 } ?? "")
        END: \(endState) — p90 \(availableEstimate(optionalNonnegativeDouble(scheduler, "p90Seconds"))); objective \(endObjective); missing \(endMissing); evidence \(schedulerEvidence)

        REBIRTH DECISION — LIVE MODEL
        CURRENT POLICY: \(rebirthPolicy)
        TARGET RUN AGE: \(noResetPolicy || rebirthExecutionHold ? "not scheduled" : formatExactDuration(rebirthTarget))
        CURRENT RUN AGE: \(formatExactDuration(rebirthElapsed))
        REMAINING: \(noResetPolicy || rebirthExecutionHold ? "no executable countdown" : formatExactDuration(rebirthRemaining))
        EXPECTED EXECUTION: \(noResetPolicy ? "none while the active challenge forbids rebirth" : rebirthExecutionHold ? "none until the event/progression model validates a reset" : wallClockEstimate(rebirthRemaining) + " local time")
        NEXT FINITE RESET CANDIDATE: \(nextPositiveText)
        NEXT MODEL EVALUATION: \(nextEvaluationText)
        ETA EVIDENCE: \(rebirthETAReason.isEmpty ? "no dedicated ETA reason emitted" : rebirthETAReason)
        \(roundExplanation)
        SELECTION REASON: \(rebirthReason)
        \(optimizerForecast)
        \(recoveryForecast)

        CHALLENGE ADMISSION — LIVE MODEL
        \(challengeGlance)

        PRODUCER / BUILD IDENTITY
        PID \(producerPid) · SESSION \(shortSession) · ACTIVE MVID \(shortBuild)
        DISK DLL \(shortDisk) · GAME DLL \(shortGame)
        ACTIVE/DISK MATCH: \(activeMatchesDisk)
        LATEST TRANSACTION: \(transactionGlance)

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
        Inventory capacity: \(inventoryUsed)/\(inventoryTotal) used, \(inventoryFree) free — \(inventoryPressure.lowercased()) pressure. \(collectionProjectedSlots) currently targeted item ID(s) need a new physical slot; reserve \(collectionReserve) includes two drop/sweep buffers. AP buys capacity first when funded, EXP only at critical pressure when AP cannot.
        Adventure stats: Power \(shortNumber(power)) / Toughness \(shortNumber(toughness))
        Energy: \(shortNumber(max(0, energyCurrent - energyIdle))) allocated / \(shortNumber(energyCurrent)) total (\(String(format: "%.1f", 100 * energyUtilization))% utilized); +\(shortNumber(energyIncome))/s; idle state: \(energyIdleReason.replacingOccurrences(of: "-", with: " "))
        Reconciliation: \(shortNumber(basicTrainingEnergy)) E in Basic Training; \(shortNumber(nonBasicTrainingEnergy)) E in Augments/other Energy systems; \(shortNumber(energyIdle)) E idle.
        Training allocation:
        \(allocationSummary)
        Long-horizon BT rule: \(state["basicTrainingLongHorizonPolicy"] as? String ?? "persistent cap investments are evaluated before immediate boss marginal value")
        Advanced Training horizon: \(advancedTrainingHorizon)
        Time Machine horizon: \(timeMachineHorizon)
        Equipment: \(loadoutDecision)
        Gear development: \(boostDecision)
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
            || category == "[INVENTORY]" || category == "[GEAR]" || category == "[COLLECTION]"
            || category == "[DISCOVERY]" { return .systemTeal }
        if category == "[MILESTONE]" { return .systemYellow }
        if category == "[YGG]" || category == "[MACGUFFIN]" { return .systemGreen }
        if category == "[QUEST]" || category == "[CHALLENGE]" { return .systemPurple }
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
            paragraph.lineSpacing = 3
            paragraph.paragraphSpacing = trimmed.isEmpty ? 5 : 1
            let isSection = !trimmed.isEmpty && trimmed == upper
                && !trimmed.hasPrefix("•") && !trimmed.hasPrefix("◆")
            if isSection { paragraph.paragraphSpacingBefore = 8 }

            var color = NSColor(calibratedWhite: 0.88, alpha: 1)
            var weight: NSFont.Weight = .regular
            var size: CGFloat = 13
            if isSection {
                color = .systemPurple; weight = .bold; size = 14
            } else if trimmed.hasPrefix("▶") {
                color = .systemGreen; weight = .semibold; size = 13.5
            } else if trimmed.hasPrefix("◆") {
                color = .systemCyan; weight = .medium
            } else if upper.contains("BLOCKED") || upper.contains("REJECTED")
                || upper.contains("NO FINITE") || upper.contains("ROUTE HOLD") {
                color = .systemOrange; weight = .medium
            }
            let row = NSMutableAttributedString(string: line + "\n", attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: size, weight: weight),
                .foregroundColor: color,
                .paragraphStyle: paragraph
            ])

            // Keep prose in one stable neutral color. Only the structural marker or
            // label receives an accent, avoiding the previous every-other-line rainbow.
            let nsLine = line as NSString
            if !isSection && !trimmed.hasPrefix("▶") && !trimmed.hasPrefix("◆") {
                let bullet = nsLine.range(of: "•")
                if bullet.location != NSNotFound && bullet.location <= 3 {
                    row.addAttributes([.foregroundColor: NSColor.systemCyan,
                                       .font: NSFont.monospacedSystemFont(ofSize: 13, weight: .bold)],
                                      range: bullet)
                } else if let colon = line.firstIndex(of: ":") {
                    let prefixLength = line.distance(from: line.startIndex, to: colon) + 1
                    if prefixLength <= 42 {
                        row.addAttributes([.foregroundColor: NSColor(calibratedWhite: 0.72, alpha: 1),
                                           .font: NSFont.monospacedSystemFont(ofSize: 13, weight: .semibold)],
                                          range: NSRange(location: 0, length: prefixLength))
                    }
                } else if let dot = line.firstIndex(of: "."),
                          line[..<dot].allSatisfy({ $0.isNumber }) {
                    let markerLength = line.distance(from: line.startIndex, to: dot) + 1
                    row.addAttribute(.font,
                                     value: NSFont.monospacedSystemFont(ofSize: 13, weight: .semibold),
                                     range: NSRange(location: 0, length: markerLength))
                }
            }
            output.append(row)
        }
        goalsTextView.textStorage?.setAttributedString(output)
    }

    /*
    KEY EVENTS FILTER

    actions.log remains the durable source. This view admits only low-frequency, state-validated
    transitions: victories, significant-digit levels, first/MAXX item discoveries, EXP/AP
    purchases, rebirths, major rewards, and completed progression. It deliberately rejects
    attempts, allocation churn, ordinary drops, merges, recovery ticks, and routing narration.
    */
    private func keyEventChunk(_ chunk: String) -> String {
        let combined = keyEventRemainder + chunk
        var lines = combined.components(separatedBy: "\n")
        if combined.hasSuffix("\n") {
            keyEventRemainder = ""
            if lines.last?.isEmpty == true { lines.removeLast() }
        } else {
            keyEventRemainder = lines.popLast() ?? ""
        }
        let selected = lines.filter(isKeyEventLine).map(keyEventDisplayLine)
        return selected.isEmpty ? "" : selected.joined(separator: "\n") + "\n"
    }

    // Live Actions keeps the validation evidence. Key Events removes that implementation
    // suffix so the sparse history reads as an event ledger rather than a diagnostic trace.
    private func keyEventDisplayLine(_ line: String) -> String {
        return line.replacingOccurrences(
            of: #"\s*\[[^\]]*(?:confirmed|verified)[^\]]*\]"#,
            with: "",
            options: [.regularExpression, .caseInsensitive]
        )
    }

    private func isKeyEventLine(_ line: String) -> Bool {
        let nsLine = line as NSString
        let match = logLineRegex.firstMatch(in: line, range: NSRange(location: 0, length: nsLine.length))
        guard let parsed = match else { return false }
        let category = nsLine.substring(with: parsed.range(at: 2)).uppercased()
        let message = nsLine.substring(with: parsed.range(at: 4)).lowercased()
        switch category {
        case "[TITAN]", "[MILESTONE]", "[DISCOVERY]", "[REBIRTH]", "[CHALLENGE]",
             "[MACGUFFIN]", "[REWARD]", "[DEATH]":
            return true
        case "[BOSS]":
            return message.contains("after native controller victory")
                || message.contains("record fight boss")
        case "[PURCHASE]":
            return message.range(of: #"\b(exp|ap)\b"#, options: .regularExpression) != nil
                || message.contains("arbitrary point")
        case "[COLLECTION]":
            return message.contains("maxxed") || message.contains("set complete")
        case "[PROGRESSION]":
            return message.contains("confirmed") || message.contains("completed")
                || message.contains("unlocked") || message.contains("consumed progression")
        case "[QUEST]":
            return message.contains("completed") || message.contains("turned in")
        case "[YGG]":
            return message.contains("harvest") || message.contains("activated")
                || message.contains("permanent")
        case "[LOOT]":
            return message.contains("ultra rare") || message.contains("legendary")
                || message.contains("macguffin") || message.contains("heart")
        default:
            return false
        }
    }

    private func keyEventsHeader() -> NSAttributedString {
        let output = NSMutableAttributedString(string: "KEY EVENTS\n", attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 15, weight: .bold),
            .foregroundColor: NSColor.systemGreen
        ])
        output.append(NSAttributedString(
            string: "Victories • significant level milestones • first/MAXX items • XP/AP purchases • major progression\n\n",
            attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 11.5, weight: .regular),
                .foregroundColor: NSColor(calibratedWhite: 0.58, alpha: 1)
            ]
        ))
        return output
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

    private func nonempty(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !value.isEmpty else { return nil }
        return value
    }

    private func optionalNonnegativeDouble(_ object: [String: Any], _ key: String) -> Double? {
        guard !(object[key] is Bool), let value = (object[key] as? NSNumber)?.doubleValue,
              value.isFinite, value >= 0 else { return nil }
        return value
    }

    private func optionalUnitDouble(_ object: [String: Any], _ key: String) -> Double? {
        guard let value = optionalNonnegativeDouble(object, key), value <= 1 else { return nil }
        return value
    }

    private func optionalNonnegativeInt(_ object: [String: Any], _ key: String) -> Int? {
        guard let value = optionalNonnegativeDouble(object, key) else { return nil }
        return Int(value)
    }

    private func firstNonempty(_ object: [String: Any], _ keys: [String]) -> String? {
        for key in keys {
            if let value = nonempty(object[key] as? String) { return value }
        }
        return nil
    }

    private func firstOptionalSeconds(_ object: [String: Any], _ keys: [String]) -> Int? {
        for key in keys {
            if let value = optionalNonnegativeInt(object, key) { return value }
        }
        return nil
    }

    private func countLabel(_ object: [String: Any], _ key: String, _ label: String) -> String? {
        guard let value = optionalNonnegativeInt(object, key) else { return nil }
        return "\(label) \(groupedInteger(value))"
    }

    private func shortHash(_ value: String?) -> String {
        guard let value = nonempty(value) else { return "Unavailable" }
        return String(value.prefix(12))
    }

    private func availableEstimate(_ seconds: Double?) -> String {
        guard let seconds = seconds, seconds.isFinite, seconds >= 0 else { return "Unavailable" }
        return formatEstimate(Int(seconds.rounded()))
    }

    private func evidenceLabel(provenance: String?, samples: Int?, confidence: Double?) -> String {
        guard let provenance = nonempty(provenance), provenance.lowercased() != "unknown"
        else { return "Unavailable" }
        var parts = [provenance]
        if let samples = samples { parts.append("\(groupedInteger(samples)) samples") }
        if let confidence = confidence {
            parts.append(String(format: "%.1f%% confidence", confidence * 100))
        }
        return parts.joined(separator: " · ")
    }

    private func stagedAuthoritySummary(_ state: [String: Any]) -> String {
        guard let staged = state["stagedAuthority"] as? [String: Any] else {
            return "Unavailable"
        }
        let routes: [(String, String)] = [
            ("verifiedReversible", "reversible"),
            ("permanentPurchases", "purchases"),
            ("moneyPit", "Money Pit"),
            ("challenges", "challenges"),
            ("difficulty", "difficulty"),
            ("titan1Through12", "T1–12"),
            ("titan13Through14", "T13–14"),
            ("move69", "MOVE69"),
            ("endSequence", "END")
        ]
        return routes.map { key, label in
            let status = staged[key] as? Bool
            return "\(label) \(status == true ? "enabled" : status == false ? "HELD" : "Unavailable")"
        }.joined(separator: " · ")
    }

    private func capacitySummary(_ state: [String: Any]) -> String {
        guard let total = optionalNonnegativeInt(state, "inventoryTotalSlots"),
              let free = optionalNonnegativeInt(state, "inventoryFreeSlots") else {
            return "Unavailable"
        }
        let used = optionalNonnegativeInt(state, "inventoryUsedSlots")
        let reserve = optionalNonnegativeInt(state, "collectionRequiredFreeReserve")
        let margin = reserve.map { free - $0 }
        let status = margin.map { $0 < 0 ? "HELD" : "available" } ?? "observed"
        return "\(status) — \(used.map(String.init) ?? "Unavailable")/\(total) used, \(free) free; reserve \(reserve.map(String.init) ?? "Unavailable"), margin \(margin.map(String.init) ?? "Unavailable"); provenance live counters, exact delivery proof Unavailable"
    }

    private func difficultyName(_ value: Int?) -> String {
        guard let value = value else { return "Unavailable" }
        switch value {
        case 0: return "Normal"
        case 1: return "Evil"
        case 2: return "Sadistic"
        default: return "Unavailable"
        }
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
