using System;
using System.Globalization;
using UnityEngine;

namespace Stellar.AutoGather;

// Auto-Gather action layer: walk-to-node (Approaching), the channeled Interaction trigger (BeginInteraction via the
// Lua bridge), and completion polling (AttrInteractionStage 3→0 / AttrCollectCounter++ / node uuid gone). Movement
// is CUT before firing and held off for the whole channel — moving aborts the channel (KB §5). All completion
// detection is by POLLING attrs; NO native Lua→C# callback (those crash BPSR). See Auto-Gather-and-Interaction.md §3-4.
internal sealed partial class AutoGatherController
{
    private const float  SettleSeconds         = 0.2f;   // hold still briefly after cutting movement before firing
    private const double ChannelTimeoutSeconds = 10.0;   // abort a stuck channel after this long
    private const double FocusRetrySeconds     = 1.5;    // strict Focus: re-fire this long for a not-yet-registered prompt before skipping

    private double _settleUntil;
    private bool   _fired;
    private bool   _sawStage;              // saw AttrInteractionStage == 3 (channel actually entered)
    private long   _startCollectCounter;   // AttrCollectCounter baseline snapshotted at fire time
    private double _channelDeadline;
    private double _focusRetryUntil;       // strict Focus: retry deadline while the focus prompt hasn't registered yet (0 = idle)

    // Classification signals from the last FireBeginInteraction (strict Focus: distinguishes a genuine out-of-Focus
    // stop from a timing miss / a non-focus node — see BeginGather). Set every fire; read only on a no-fire in Focus mode.
    private bool   _lastFocusPresent;      // focus interaction entry existed for the target uuid
    private bool   _lastFocusCond;         // that focus entry's CheckCondition passed
    private bool   _lastFound;             // any entry matched the target uuid (prompt registered)
    private double _lastFocusBal = -1;     // current Focus (item 20003) balance; -1 = unreadable

    // ── Seek: pick the nearest non-blacklisted node of the locked type ───────
    private void TickSeek(Vector3 localPos)
    {
        // Strict-Focus out-of-Focus gate: if we're in Focus mode and can't afford the LOCKED type's focus cost, STOP
        // the whole loop here — BEFORE walking to / blacklisting any node. This is the reliable detector (independent of
        // whether the game registers the per-node focus interaction entry when the player is broke). Throttled ~1s.
        if (_focusMode)
        {
            int cost = LockedFocusCost;                       // 0 = normal-only/unresolved → gate doesn't apply
            double now = _services.Framework.TimeNow;
            if (cost > 0 && now - _focusGateTimer >= 1.0)
            {
                _focusGateTimer = now;
                if (TryReadFocusBalance(out int bal) && bal < cost)
                {
                    _services.Log.Info($"[Gather] out of Focus at seek (bal={bal} < cost={cost}) — stopping");
                    StopOutOfFocus();
                    return;
                }
            }
        }

        if (TrySelectNearest(localPos, out var uuid, out var pickRange, out var name))
        {
            _targetUuid      = uuid;
            _targetPickRange = pickRange;
            _uuidConfig.TryGetValue(uuid, out _targetConfigId);
            // Cache the target's Focus cost (0 = unknown) for BeginGather's out-of-Focus affordability test. The config
            // is already resolved into _cfgCache by the roster rebuild that selected this node.
            _targetFocusCost = (_targetConfigId != 0 && _cfgCache.TryGetValue(_targetConfigId, out var tcfg)) ? tcfg.Cost : 0;
            _state = GState.Approaching;
            _services.Log.Info($"[Gather] target node {uuid} '{name}' pick={pickRange:F1}");
            SetStatus(_loc.TFormat("ag.status.seeking", name), "seeking");
        }
        else
        {
            _moveDriver.Stop(this);
            SetStatus(_lockedConfigId == 0 ? _loc.T("ag.status.pickType") : _loc.TFormat("ag.status.noNodes", LockedName), "no-nodes");
        }
    }

    // ── Approach: route around obstacles toward the node until inside the stop distance ─────────────────────
    private void TickApproach(object localEnt, Vector3 localPos)
    {
        var nodeEnt = GetEntity(_targetUuid);
        if (nodeEnt == null) { OnNodeGone("node gone (approach)"); return; }
        if (!TryGetPos(nodeEnt, out var nodePos)) { SetStatus("node pos unavailable"); return; }

        float dist = Vector3.Distance(localPos, nodePos);

        // Universal safety net: skip a node we can't reach. StuckTooLong = IsStuckInPlace (hard wall); NoProgress =
        // distance-to-node not improving for ~2.5s (catches WALL-SLIDING, where IsStuckInPlace doesn't trip).
        if (StuckTooLong(localEnt) || NoProgress(dist)) { OnNodeGone("stuck — skipping"); return; }

        float stop = _targetPickRange > 0.1f ? _targetPickRange : DefaultStopDistance;
        if (dist <= stop)
        {
            ClearPath();                            // clear the server route
            _moveDriver.Stop(this);                 // CUT movement — moving aborts the channel
            _state       = GState.Gathering;
            _fired       = false;
            _settleUntil = _services.Framework.TimeNow + SettleSeconds;
            _services.Log.Info($"[Gather] in range ({dist:F1}m ≤ {stop:F1}m) — settling before BeginInteraction");
            SetStatus(_loc.T("ag.status.inRangeSettling"), "settling");
            return;
        }

        // Straight-line toward the node; the watchdogs above skip it if it turns out to be unreachable.
        Vector3 steer = ComputeApproachSteer(localEnt, localPos, nodePos);
        if (_approachSkip) { OnNodeGone("unreachable — skipping"); return; }
        if (_moveDriver.TryDrive(this, localPos, steer, 1f))
            SetStatus(_loc.TFormat("ag.status.approaching", dist.ToString("F1")), "approaching");
        else
            SetStatus("move deferred (shared driver busy or arrived)", "deferred");
    }

    // ── Gather: settle → fire → completion (progress-end primary; attr-poll fallback) → done ────────────────
    private void TickGather(object localEnt)
    {
        _moveDriver.Stop(this);   // keep movement cut for the whole channel

        if (!_fired)
        {
            if (_services.Framework.TimeNow < _settleUntil) { SetStatus(_loc.T("ag.status.settling"), "settling"); return; }
            BeginGather();
            return;
        }

        // PRIMARY: the progress-bar-filled hook (immediate, tracks real gather speed — KB §4.5).
        if (ProgressEndedUuid == _targetUuid) { ProgressEndedUuid = 0; OnGatherComplete("progress end"); return; }

        // FALLBACKS: node despawn (LATE per KB §4.4), stage 3→0, collect-counter++.
        if (GetEntity(_targetUuid) == null) { OnGatherComplete("node consumed"); return; }

        long stage   = GetAttrLong(localEnt, _boxInteractionStage);
        long counter = GetAttrLong(localEnt, _boxCollectCounter);
        if (stage == 3) _sawStage = true;

        if (_sawStage && stage == 0)            { OnGatherComplete("stage 3→0"); return; }
        if (counter > _startCollectCounter)     { OnGatherComplete("collect counter++"); return; }

        if (_services.Framework.TimeNow >= _channelDeadline)
        {
            _services.Log.Info($"[Gather] channel timeout uuid={_targetUuid} (sawStage={_sawStage} stage={stage})");
            AbortActiveChannel("timeout");
            AddBlacklist(_targetUuid);
            _state = GState.Seeking; _targetUuid = 0;
            return;
        }
        SetStatus(_loc.T("ag.status.gathering"), "gathering");
    }

    // Fire BeginInteraction for the target node. Snapshots the collect-counter baseline first, then confirms the
    // option is registered in interaction_data and begins it — all inside one Lua chunk (avoids marshalling the list
    // and keeps the uuid inside Lua). Instruments whether the option was found.
    private void BeginGather()
    {
        var local = GetEntity(_services.CombatSnapshot.LocalEntityId.Value);
        _startCollectCounter = local != null ? GetAttrLong(local, _boxCollectCounter) : 0;
        _sawStage = false;
        ProgressEndedUuid = 0;   // clear any stale progress-end so only a POST-fire completion counts

        bool fired = FireBeginInteraction(_targetUuid);
        if (!fired)
        {
            // Strict Focus mode used to STOP the whole loop on ANY no-fire, conflating four cases — only one of which
            // is真 out-of-Focus. Classify using the focus signals + the target's known Focus cost and stop ONLY on a
            // genuine out-of-Focus; otherwise retry (timing) or skip the node (not focus-gatherable while we HAVE Focus).
            if (_focusMode)
            {
                bool   focusPresent = _lastFocusPresent;
                bool   focusCond    = _lastFocusCond;
                bool   found        = _lastFound;
                double bal          = _lastFocusBal;

                // Unknown cost (0) → only a hard 0 balance counts as broke; unknown balance (-1) → never claim broke.
                bool affordable = _targetFocusCost <= 0
                    ? (bal != 0)
                    : (bal < 0 ? true : bal >= _targetFocusCost);

                // (4) GENUINE out-of-Focus: focus entry present, its CheckCondition failed, and we can't afford it → STOP
                // the loop. This is a secondary safety net; the seek-time gate in TickSeek is the primary, reliable detector.
                if (focusPresent && !focusCond && !affordable)
                {
                    _focusRetryUntil = 0;
                    StopOutOfFocus();
                    return;
                }

                double now = _services.Framework.TimeNow;

                // (1) TIMING: nothing matched the uuid yet (interaction_data empty / node prompt not registered at fire
                // time). The one-shot fire makes this common → retry briefly (re-fires next tick while _fired stays
                // false), then skip. NEVER "out of Focus" for this.
                if (!found)
                {
                    if (_focusRetryUntil == 0) _focusRetryUntil = now + FocusRetrySeconds;
                    if (now < _focusRetryUntil) { SetStatus(_loc.T("ag.status.focusLoading"), "focus-wait"); return; }
                    _services.Log.Info("[Gather] focus option didn't appear (have Focus) — skipping node");
                    AddBlacklist(_targetUuid);
                    _state = GState.Seeking; _targetUuid = 0; _focusRetryUntil = 0;
                    return;
                }

                // (2)+(3) Node not focus-gatherable while we HAVE Focus (normal-only node: found && !focusPresent; or a
                // focus entry whose cond failed for a NON-focus gate: level/recipe/cooldown → present && !cond && affordable)
                // → SKIP the node and keep farming. Do NOT stop the loop.
                _services.Log.Info($"[Gather] node not focus-gatherable (present={focusPresent} cond={focusCond} bal={bal} cost={_targetFocusCost}) — skipping (Focus not exhausted)");
                AddBlacklist(_targetUuid);
                _state = GState.Seeking; _targetUuid = 0; _focusRetryUntil = 0;
                return;
            }

            // NORMAL mode (unchanged): no option registered / no BeginInteraction call (uuid mismatch AND list not a
            // single gather option, or empty list) → nothing started, so DON'T abort; blacklist + reseek.
            AddBlacklist(_targetUuid);
            _state = GState.Seeking; _targetUuid = 0;
            return;
        }

        _fired = true;
        _focusRetryUntil = 0;   // successful fire → clear any pending retry deadline so it can't leak to the next node
        _channelDeadline = _services.Framework.TimeNow + ChannelTimeoutSeconds;
        SetStatus(_loc.T("ag.status.gathering"), "gathering");
    }

    // Lua find-and-begin with Focus/Normal option selection (KB §5b). A focus-capable node registers TWO
    // interaction_data entries for the same uuid differing by templateId; we classify each by its button id
    // (GetReplaceBtnId ∈ {1098,1102}=Focus, {1096,1097}=Normal — direct getter, no table lookup) and pick per the
    // Focus/Normal toggle. Focus mode is STRICT: it fires ONLY when the focus entry's CheckCondition passes; if the
    // focus entry fails or is absent it chooses nothing (fired=false). Rather than a single blunt "stop" signal (which
    // conflated four cases — timing / normal-only node / non-focus gate / real out-of-Focus — and caused false stops),
    // it exposes RICH signals so BeginGather can classify: __ag_focus_present (a focus entry existed), __ag_focus_cond
    // (its CheckCondition passed), __ag_found (any entry matched the uuid), __ag_focus_bal (current Focus balance).
    // Fires the chosen entry's LIVE four fields (⚠️ interactionCfgId is assigned live — never static). Sole-option
    // fallback kept for the uuid-compare (C2). Reads back diagnostics (found/fired/sole/count/mode/tpl/btn + per-entry diag string) — this
    // feature is UNVALIDATED, so the first fire logs every matched entry's tpl/btn/cfg/seat/cond to refine from
    // real values. DoString is synchronous → globals are set before we read them. Returns whether it fired.
    private bool FireBeginInteraction(long uuid)
    {
        if (!_services.Lua.Ready) return false;
        _services.Lua.DoString(
            "pcall(function()" +
            " rawset(_G,'__ag_found', nil) rawset(_G,'__ag_fired', nil) rawset(_G,'__ag_sole', nil)" +
            " rawset(_G,'__ag_focus_present', nil) rawset(_G,'__ag_focus_cond', nil) rawset(_G,'__ag_focus_bal', -1)" +
            " rawset(_G,'__ag_count', 0) rawset(_G,'__ag_mode', '') rawset(_G,'__ag_tpl', 0) rawset(_G,'__ag_btn', 0) rawset(_G,'__ag_diag', '')" +
            " local uuid = " + uuid.ToString(CultureInfo.InvariantCulture) +
            " local focus = " + (_focusMode ? "true" : "false") +
            // Current Focus (item 20003) balance, read in its OWN pcall so a VM hiccup can't abort the fire. -1 = unreadable.
            // DOT call: GetItemTotalCount has no self param — a colon passes the VM as configId and drops 20003 (see condition_helper.lua:623).
            " pcall(function() rawset(_G,'__ag_focus_bal', (Z.VMMgr.GetVM('items')).GetItemTotalCount(20003)) end)" +
            " local list = ((Z.DataMgr).Get)(\"interaction_data\"):GetData()" +
            " if list == nil then return end" +
            " local n = #list" +
            " rawset(_G,'__ag_count', n)" +
            " local function classify(e)" +
            "  local btn = e:GetReplaceBtnId()" +
            "  local cond = (Z.InteractionMgr):CheckCondition(e:GetUuid(), e:GetInteractionCfgId(), e:GetSeatGroupIndex(), false)" +
            "  local rec = { e = e, tpl = e:GetTemplateId(), btn = btn, cond = cond }" +
            "  return rec, (btn == 1098 or btn == 1102), (btn == 1096 or btn == 1097)" +
            " end" +
            " local focusE, normalE, anyE, diag = nil, nil, nil, ''" +
            " for i = 1, n do" +
            "  local e = list[i]" +
            "  if e ~= nil and e:GetUuid() == uuid then" +
            "   rawset(_G,'__ag_found', true)" +
            "   local rec, isF, isN = classify(e)" +
            "   anyE = anyE or rec" +
            "   if isF then focusE = focusE or rec elseif isN then normalE = normalE or rec end" +
            "   diag = diag .. 'tpl=' .. tostring(rec.tpl) .. ' btn=' .. tostring(rec.btn) .. ' cfg=' .. tostring(e:GetInteractionCfgId()) .. ' seat=' .. tostring(e:GetSeatGroupIndex()) .. ' cond=' .. tostring(rec.cond) .. '; '" +
            "  end" +
            " end" +
            // Sole-option fallback (uuid-FORMAT mismatch: the node's own prompt is the only registered entry).
            // GATE it to gather buttons: classify list[1] FIRST and accept only when it's a Focus/Normal gather
            // button (btn ∈ {1098,1102,1096,1097}); otherwise reject (leave anyE/focusE/normalE nil → nothing fires
            // → BeginGather blacklists + reseeks, never talking to it). WHY SAFE: a real gather node still classifies
            // as a gather button and still fires; only NON-gather sole prompts (a nearby NPC talk prompt, etc.) are
            // now rejected — the old code fired list[1] unconditionally, so in Normal mode a nearby NPC prompt became
            // the sole entry and we talked to the NPC instead of gathering. TRADE-OFF: a gather node using a button id
            // outside {1096,1097,1098,1102} that was ALSO the sole entry would now be rejected too — the SOLE-REJECT
            // diag logs its btn so that's diagnosable; preventing NPC-talk misfires is the priority.
            " if anyE == nil and n == 1 then" +
            "  local rec, isF, isN = classify(list[1])" +
            "  if isF or isN then" +
            "   rawset(_G,'__ag_sole', true)" +
            "   anyE = rec" +
            "   if isF then focusE = rec elseif isN then normalE = rec end" +
            "   diag = diag .. 'SOLE tpl=' .. tostring(rec.tpl) .. ' btn=' .. tostring(rec.btn) .. ' cond=' .. tostring(rec.cond) .. '; '" +
            "  else" +
            "   diag = diag .. 'SOLE-REJECT(non-gather btn=' .. tostring(rec.btn) .. '); '" +
            "  end" +
            " end" +
            " rawset(_G,'__ag_diag', diag)" +
            // Report the focus entry's state so C# (BeginGather) can tell a real out-of-Focus from timing / a non-focus node.
            " rawset(_G,'__ag_focus_present', (focusE ~= nil) and true or false)" +
            " rawset(_G,'__ag_focus_cond', (focusE ~= nil and focusE.cond) and true or false)" +
            " local chosen, mode = nil, ''" +
            " if focus then" +
            "  if focusE ~= nil and focusE.cond then chosen = focusE mode = 'Focus' end" +
            " else" +
            "  if normalE ~= nil then chosen = normalE mode = 'Normal'" +
            "  elseif focusE ~= nil then chosen = focusE mode = 'Focus'" +
            "  elseif anyE ~= nil then chosen = anyE mode = '?' end" +
            " end" +
            " if chosen ~= nil then" +
            "  local c = chosen.e" +
            "  ;(Z.InteractionMgr):BeginInteraction(c:GetUuid(), c:GetInteractionCfgId(), c:GetTemplateId(), c:GetSeatGroupIndex())" +
            "  rawset(_G,'__ag_fired', true) rawset(_G,'__ag_mode', mode) rawset(_G,'__ag_tpl', chosen.tpl) rawset(_G,'__ag_btn', chosen.btn)" +
            " end" +
            " end)");

        // ── Commented manual-resolve FALLBACK (only if interaction_data is empty at our stop distance) ──
        //   templateId = ZInteractionMgr.GetTemplateId(nodeEntity)  (static, dump 239170)
        //   interactionCfgId = node attr AttrDynamicInteractionId=365 (read via GetLuaAttr / validate GetAttr<long>)
        //   seatGroupIndex = -1  →  (Z.InteractionMgr):BeginInteraction(uuid, cfgId, templateId, -1)

        _services.Lua.TryReadGlobalBool("__ag_found", out var found);
        _services.Lua.TryReadGlobalBool("__ag_fired", out var fired);
        _services.Lua.TryReadGlobalBool("__ag_sole",  out var sole);
        _services.Lua.TryReadGlobalNumber("__ag_count", out var count);
        _services.Lua.TryReadGlobalNumber("__ag_tpl",   out var tpl);
        _services.Lua.TryReadGlobalNumber("__ag_btn",   out var btn);
        string mode = _services.Lua.ReadGlobalString("__ag_mode") ?? "";
        string diag = _services.Lua.ReadGlobalString("__ag_diag") ?? "";

        // Focus classification signals (strict Focus): BeginGather uses these + _targetFocusCost to decide stop vs
        // retry vs skip instead of a single "stop" sentinel that couldn't tell those apart.
        _services.Lua.TryReadGlobalBool("__ag_focus_present", out var focusPresent);
        _services.Lua.TryReadGlobalBool("__ag_focus_cond",    out var focusCond);
        _services.Lua.TryReadGlobalNumber("__ag_focus_bal",   out var focusBal);
        _lastFocusPresent = focusPresent;
        _lastFocusCond    = focusCond;
        _lastFound        = found;
        _lastFocusBal     = focusBal;
        if (_focusMode && !fired)
            _services.Log.Info($"[Gather] focus no-fire: present={focusPresent} cond={focusCond} found={found} bal={focusBal} cost={_targetFocusCost}");

        if (sole && !found) _services.Log.Info("[Gather] uuid match failed — using sole option");
        if (!string.IsNullOrEmpty(diag)) _services.Log.Info($"[Gather] entries: {diag}");
        if (fired) _services.Log.Info($"[Gather] fired mode={mode} tpl={(int)tpl} btn={(int)btn}");
        _services.Log.Info($"[Gather] BeginInteraction uuid={uuid} found={found} fired={fired} sole={sole} listCount={(int)count}");
        return fired;
    }

    // A gather finished → BLACKLIST the just-gathered node immediately, then reseek. The server despawns a
    // gathered node from entityDict_ LATE (KB §4.4), so without this the loop re-selects the still-present depleted
    // node, walks back, fails, then advances (the visible delay). ~15s blacklist; a refreshed node returns as a new uuid.
    private void OnGatherComplete(string via)
    {
        _services.Log.Info($"[Gather] complete via {via} (uuid={_targetUuid})");
        _fired = false;
        ProgressEndedUuid = 0;
        ClearPath();
        if (_targetUuid != 0) AddBlacklist(_targetUuid);
        _state = GState.Seeking;
        _targetUuid = 0;
        SetStatus(_loc.T("ag.status.gathered"), "done");
    }

    // Node dropped out of entityDict_ during approach (someone else took it / it depleted) → blacklist briefly + reseek.
    private void OnNodeGone(string why)
    {
        _services.Log.Info($"[Gather] {why} (uuid={_targetUuid})");
        _moveDriver.Stop(this);   // R1: release the stick before reseeking (avoid one stale-direction frame)
        ProgressEndedUuid = 0;
        ClearPath();
        if (_targetUuid != 0) AddBlacklist(_targetUuid);
        _state = GState.Seeking; _targetUuid = 0;
        SetStatus(_loc.T("ag.status.nodeGone"), "node-gone");
    }

    // Abort whatever channel might be active (best-effort; safe to call when nothing is channeling). Uses the game's
    // own AbortInteractionByUI (interaction_skip_window_view.lua:146).
    private void AbortActiveChannel(string why)
    {
        ProgressEndedUuid = 0;
        if (!_fired) return;
        _fired = false;
        _services.Log.Info($"[Gather] abort channel ({why})");
        try
        {
            if (_services.Lua.Ready)
                _services.Lua.DoString("pcall(function() (Z.InteractionMgr):AbortInteractionByUI() end)");
        }
        catch (Exception ex) { LogErrOnce("abort: " + (ex.InnerException?.Message ?? ex.Message)); }
    }
}
