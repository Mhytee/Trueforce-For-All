--
-- TF4ALL Telemetry mod
-- Physics channel for the Trueforce For All SimHub plugin.
--
-- Sends per-wheel suspension position, steering angle and ground contact,
-- plus engine load, over its own named pipe at physics cadence. Coexists
-- with SimHub's own SHTelemetry mod (different pipe, different fields);
-- keep both installed. The plugin hosts the pipe server; when the plugin
-- is not running the open simply fails and the mod retries.
--
-- Everything the game is asked for is guarded: a field that does not exist
-- on this game version is skipped, never a crash. On first vehicle entry
-- the mod prints the wheel object's field names to the game log once, so
-- an unexpected layout can be mapped from a log file.
--

TF4ALLTelemetry = {}
-- Keep in sync with modDesc.xml <version> (build-mod.py verifies). The
-- plugin compares this against its shipped version to tell the user when
-- the game is still running an older copy (mods load only at game start).
local MOD_VERSION = "0.2.24"
local ctx = {
	pipeName = "\\\\.\\pipe\\TF4ALLTelemetry",
	file = nil,
	updateDt = 0,
	reopenDt = 1000,   -- first open attempt is immediate
	connAgeDt = 0,
	wheelKeysDumped = false,
	-- Hydraulic-motion detection state: previous attacher-joint moveAlpha
	-- values, keyed by joint index; reset on vehicle change so a switch
	-- can't read one vehicle's alphas against another's.
	jointAlpha = {},
	jointAlphaCfg = nil,
	-- Previous fold anim times (normalized 0..1), keyed 0 for the vehicle
	-- itself and by attached-implement walk index; feeds the travel-rate
	-- signal the same way jointAlpha does. Reset with jointAlpha.
	foldT = {},
	-- Discrete-event counter (mod 0.2.16): bumps once per observed
	-- mechanism toggle with no measurable travel (a combine's straw-swath
	-- flap). Session-monotonic, NEVER reset on vehicle change: a reset
	-- would read as a change plugin-side and fire a stale thud.
	implEvt = 0,
	-- Previous discrete states behind implEvt, keyed per unit like foldT;
	-- THESE reset with jointAlpha so re-entry can't fire a stale edge.
	statePrev = {},
	-- Previous moving-tool rotation/translation per unit+tool (mod 0.2.18:
	-- loader/crane/telehandler arms). Reset with jointAlpha.
	toolPrev = {},
	-- One-shot moving-tools field probe, same spirit as the fn probe.
	mtProbeDone = false,
	-- Phase ends (mod 0.2.22): per-component motion bookkeeping so each
	-- stage of a multi-part cycle (lift, then fold wings, then fold again)
	-- lands its own thud instead of only the final settle. compState holds
	-- {moving, lastRate, still} per tracked component and resets with
	-- jointAlpha; implPhase is session-monotonic like implEvt and NEVER
	-- resets on vehicle change (a reset reads as a bump plugin-side).
	compState = {},
	implPhase = 0,
	implPhSpd = 0,
	phaseCool = 0,
	-- Wheel-rotation state for the derived slip signal: previous lastXDrive
	-- angle + timestamp per wheel index (FS25 exposes no slip value, so we
	-- compute wheel surface speed from the rotation angle the game uses to
	-- spin the wheel mesh). Reset with jointAlpha on vehicle change.
	xdrvPrev = {},
	xdrvDt = 0,
	-- Per-wheel EMA over the instantaneous surface speed: lastXDrive is
	-- network-quantized (X_DRIVE_NUM_BITS), advancing in steps slower than
	-- our sampling, so raw deltas read as zeros with spikes; the EMA
	-- converges on the true rotation rate.
	wsEma = {},
	slipProbeDt = 0,
	slipProbeDone = false,
	-- One-shot pipe/swath field probe (mod 0.2.16), same spirit as the
	-- wheel-field dump: prints what the game actually exposes so a renamed
	-- spec field is readable from a log file instead of a silent no-op.
	fnProbeDone = false,
}

local function finite(x)
	return x ~= nil and type(x) == "number" and x == x and x > -math.huge and x < math.huge
end

function TF4ALLTelemetry:getCurrentVehicle()
	if g_minModDescVersion ~= nil and g_minModDescVersion >= 90 then
		local p = g_currentMission.playerSystem.playersByUserId[g_currentMission.playerUserId]
		if p ~= nil then
			return p.getCurrentVehicle()
		end
		return nil
	end
	return g_currentMission.controlledVehicle
end

-- One walk over the vehicle and its attached implements gathering the
-- drag-relevant state:
--   impl: true while anything is actually WORKING, lowered into the
--         ground (plows, cultivators) or powered on (PTO tools, mowers,
--         balers, a combine's thresher).
--   fill: highest fill fraction (0..1) across ATTACHED units only, so a
--         loaded trailer counts as hauling but the tractor's own fuel
--         tank never does.
--   towKg: summed live mass of the attached units (cargo included),
--         informational for now.
local function gatherAttachState(vehicle)
	local impl, fill, massKg = false, 0, 0
	local function unitOn(u)
		if u == nil then
			return false
		end
		if u.getIsLowered ~= nil then
			local o, r = pcall(u.getIsLowered, u)
			if o and r == true then
				return true
			end
		end
		if u.getIsTurnedOn ~= nil then
			local o, r = pcall(u.getIsTurnedOn, u)
			if o and r == true then
				return true
			end
		end
		return false
	end
	local function unitFill(u)
		if u == nil or u.getFillUnits == nil
			or u.getFillUnitFillLevelPercentage == nil then
			return 0
		end
		local best = 0
		local o, units = pcall(u.getFillUnits, u)
		if o and type(units) == "table" then
			for i in ipairs(units) do
				local ok2, pct = pcall(u.getFillUnitFillLevelPercentage, u, i)
				if ok2 and finite(pct) and pct > best then
					best = pct
				end
			end
		end
		if best > 1 then
			best = 1
		end
		return best
	end
	-- Hydraulic motion (mod 0.2.8): true while any lower/raise or fold is
	-- animating. Two signals: an implement's fold direction (big arms and
	-- wings deploying) and the tractor's attacher-joint moveAlpha changing
	-- between its raised and lowered limits (the three-point linkage in
	-- transit, generic across implement types).
	-- Travel rate (mod 0.2.14): the same deltas over the frame gap give how
	-- FAST the motion runs, as fraction of full travel per second (~0.5-1.0
	-- for a three-point lower, ~0.1 for a big slow fold). Max across all
	-- moving parts; rides the plugin's hydraulic-hum pitch.
	local moving = false
	-- Machine-cycle motion (joints, folds, pipe): true alongside moving.
	-- Moving WITHOUT cycle means manual-only motion (a stick-driven arm,
	-- mod 0.2.19); the plugin lands those settles quieter.
	local cycle = false
	local rate = 0
	local dtSec = nil
	if finite(ctx.frameDt) and ctx.frameDt >= 5 and ctx.frameDt <= 100 then
		dtSec = ctx.frameDt / 1000
	end
	-- Per-component stall detection (mod 0.2.22). Every tracked part
	-- (each joint, each foldable, each moving tool) reports its OWN rate
	-- here; a part that was moving and has been still for a few frames
	-- has finished its stage. A fold keeps its commanded direction set
	-- across an internal pause, so the aggregate motion stays true while
	-- the part's rate drops to zero: that gap is the stage boundary.
	-- Stalls are collected and judged after the walk, once it is known
	-- whether anything else is still moving (if nothing is, the motion
	-- simply ended and the plugin's own settle covers it).
	local stalls = {}
	local function noteComp(key, compRate, isMoving)
		if isMoving == nil then
			isMoving = compRate > 0
		end
		local st = ctx.compState[key]
		if st == nil then
			st = { moving = false, lastRate = 0, still = 0, run = 0 }
			ctx.compState[key] = st
		end
		if isMoving then
			st.moving = true
			st.still = 0
			st.run = st.run + 1
			if compRate > 0 then
				st.lastRate = compRate
			end
		elseif st.moving then
			st.still = st.still + 1
			-- 4 frames (~40 ms at the mod's cadence): long enough that the
			-- end of a whole cycle clears its commanded flag first (so the
			-- final settle is not preceded by a phantom stage thud), short
			-- enough to still feel attached to the part stopping.
			if st.still >= 4 then
				st.moving = false
				-- Only a part that genuinely travelled counts as a stage.
				-- Loader tools track the linkage and dither a frame or two
				-- over rough ground; that is not a mechanism finishing, and
				-- without this gate alternating parts would machine-gun the
				-- wheel at the cooldown rate.
				if st.run >= 8 then
					table.insert(stalls, st.lastRate)
				end
				st.run = 0
			end
		end
	end
	-- Stable per-unit key. The attached-implement walk index shifts when
	-- something is detached, which hands one unit's tracked state to
	-- another: the rate tables absorb that through their stale-delta
	-- swallow, but compState's moving flag would latch a fake edge and
	-- stall into a phantom stage thud. rootNode is instance-unique, the
	-- same identity the vehicle-change reset relies on.
	local function unitKey(u, fallback)
		local rn = u ~= nil and u.rootNode or nil
		if rn ~= nil then
			return tostring(rn)
		end
		return "i" .. tostring(fallback)
	end
	local function unitFolding(u, key)
		local ok, r = pcall(function()
			local sf = u.spec_foldable
			if sf == nil then
				return false
			end
			local dir = sf.foldMoveDirection
			local folding = dir ~= nil and dir ~= 0
			-- The 0.2.23 fold diagnostics (whole-cycle trace + field dumps)
			-- did their job and are gone: FS25 exposes no per-part anim
			-- bounds (foldingParts entries came back nil..nil) and the fold
			-- timeline runs at a constant rate with no pause between
			-- stages, so stage edges are not visible in this signal at all.
			-- Re-add them from git history if the stage work resumes.
			-- Fold speed from the normalized anim time, same derivative
			-- trick as the joint alphas below.
			local fr = 0
			local fmov = false
			local t = sf.foldAnimTime
			if finite(t) then
				local prev = ctx.foldT[key]
				ctx.foldT[key] = t
				if prev ~= nil then
					local d = math.abs(t - prev)
					-- A real per-frame delta stays small; a big jump is a
					-- stale prev (implement reindex, reconnect gap), not
					-- fast motion. Swallow it, keep the fresh prev.
					if d > 0.0005 and d < 0.25 then
						-- Moving is judged on the DELTA, not the derived
						-- rate: a frame gap outside the sane window leaves
						-- dtSec nil, and a fold mid-travel must not read as
						-- a finished stage just because its rate was
						-- unavailable (the joints take the same care).
						fmov = true
						if dtSec ~= nil then
							fr = d / dtSec
							if fr > rate then
								rate = fr
							end
						end
					end
					-- Stage edges from the fold's own part table, when the
					-- game exposes one: as the animation sweeps past an
					-- INTERIOR part boundary that part has just finished,
					-- which is a stage end even if the animation never
					-- pauses. The outer bounds are the cycle's own start and
					-- end, and the settle already covers those.
					if fmov then
						local fp = sf.foldingParts
						if type(fp) == "table" then
							local lo, hi = prev, t
							if lo > hi then
								lo, hi = hi, lo
							end
							for _, part in ipairs(fp) do
								local function edge(x)
									if finite(x) and x > 0.02 and x < 0.98
										and x > lo and x <= hi then
										table.insert(stalls, fr)
									end
								end
								if part ~= nil then
									edge(part.startAnimTime)
									edge(part.endAnimTime)
								end
							end
						end
					end
				end
			end
			-- Rate, not the commanded flag: a fold paused between stages
			-- still reads as commanded, and that pause is exactly the stall
			-- the phase detector is looking for.
			noteComp("f" .. key, fr, fmov)
			return folding
		end)
		return ok and r == true
	end
	-- Pipe travel (mod 0.2.16): a combine or auger wagon's unloading pipe
	-- swings between discrete states through an animation; while current
	-- and target state differ the pipe is in motion. No joint or fold
	-- moves, so the detectors above never see it.
	local function unitPipeMoving(u)
		local ok, r = pcall(function()
			local sp = u.spec_pipe
			return sp ~= nil and sp.currentState ~= nil and sp.targetState ~= nil
				and sp.currentState ~= sp.targetState
		end)
		return ok and r == true
	end
	-- Manually driven arms (mod 0.2.18): loader, crane and telehandler
	-- tools are player-controlled cylinders (spec_cylindered.movingTools),
	-- invisible to the joint/fold/pipe detectors. Track each tool's
	-- rotation and translation deltas, normalized against its travel
	-- limits where the game exposes them (nominal spans otherwise) so the
	-- rate lands in the same fraction-of-full-travel unit as the joints.
	-- The stale-prev swallow (d < 0.25) also folds a continuous-slew
	-- crane's angle wrap back to silence instead of a spike.
	-- FS stores a tool's curRot/curTrans as 3-component tables updated
	-- IN PLACE (on-wheel probe 2026-08-10: curRot=table, rotMin=nil), so
	-- the components are read out and COPIED each frame: finite() on the
	-- table itself read the whole detector dead (the mod 0.2.19 crane
	-- bug), and keeping the table reference would compare the game's
	-- table to itself. All three components are tracked rather than
	-- guessing the driven axis; the idle ones simply never move.
	local function toolAxes(v)
		if type(v) == "table" then
			return v[1], v[2], v[3]
		end
		return v, nil, nil
	end
	local function unitToolMotion(u, key)
		pcall(function()
			local scy = u.spec_cylindered
			if scy == nil or type(scy.movingTools) ~= "table" then
				return
			end
			if not ctx.mtProbeDone and #scy.movingTools > 0 then
				ctx.mtProbeDone = true
				local p1 = scy.movingTools[1]
				local pr1, pr2, pr3 = toolAxes(p1.curRot)
				local pt1, pt2, pt3 = toolAxes(p1.curTrans)
				print(string.format(
					"TF4ALLTelemetry: mt probe: n=%d rot=%s/%s/%s trans=%s/%s/%s rotMin=%s rotMax=%s",
					#scy.movingTools, tostring(pr1), tostring(pr2), tostring(pr3),
					tostring(pt1), tostring(pt2), tostring(pt3),
					tostring(p1.rotMin), tostring(p1.rotMax)))
			end
			for ti, mt in ipairs(scy.movingTools) do
				local k = "mt" .. key .. "_" .. ti
				local prev = ctx.toolPrev[k]
				local r1, r2, r3 = toolAxes(mt.curRot)
				local t1, t2, t3 = toolAxes(mt.curTrans)
				local cur = { r1 = r1, r2 = r2, r3 = r3, t1 = t1, t2 = t2, t3 = t3 }
				ctx.toolPrev[k] = cur
				if prev ~= nil then
					-- Per-tool motion, so one arm segment finishing while
					-- another still swings reads as a stage end.
					local tr, tmov = 0, false
					local function comp(c, p, mn, mx, span)
						if not finite(c) or not finite(p) then
							return
						end
						if finite(mn) and finite(mx) and mx > mn then
							span = mx - mn
						end
						local d = math.abs(c - p) / span
						if d > 0.0005 then
							moving = true
							tmov = true
							if dtSec ~= nil and d < 0.25 then
								local r = d / dtSec
								if r > tr then
									tr = r
								end
								if r > rate then
									rate = r
								end
							end
						end
					end
					comp(cur.r1, prev.r1, mt.rotMin, mt.rotMax, 2.5)
					comp(cur.r2, prev.r2, mt.rotMin, mt.rotMax, 2.5)
					comp(cur.r3, prev.r3, mt.rotMin, mt.rotMax, 2.5)
					comp(cur.t1, prev.t1, mt.transMin, mt.transMax, 1.0)
					comp(cur.t2, prev.t2, mt.transMin, mt.transMax, 1.0)
					comp(cur.t3, prev.t3, mt.transMin, mt.transMax, 1.0)
					noteComp(k, tr, tmov)
				end
			end
		end)
	end
	-- Discrete mechanism toggles (mod 0.2.16): state flips with no
	-- measurable travel, e.g. the straw-swath flap. Each observed change
	-- bumps the session-monotonic implEvt counter; the plugin lands a
	-- light thud per bump.
	local function unitStateEvents(u, key)
		pcall(function()
			local sc = u.spec_combine
			if sc ~= nil and sc.isSwathActive ~= nil then
				local k = "sw" .. key
				local prev = ctx.statePrev[k]
				local cur = sc.isSwathActive == true
				ctx.statePrev[k] = cur
				if prev ~= nil and prev ~= cur then
					ctx.implEvt = ctx.implEvt + 1
				end
			end
		end)
	end
	pcall(function()
		local cfg = vehicle.configFileName
		-- configFileName is a model id, not an instance id: two identical
		-- combines share it, so switching between them must also reset the
		-- prev-state tables or stale edges fire. rootNode is instance-unique.
		local iid = vehicle.rootNode
		if ctx.jointAlphaCfg ~= cfg or ctx.jointAlphaIid ~= iid then
			ctx.jointAlphaCfg = cfg
			ctx.jointAlphaIid = iid
			ctx.jointAlpha = {}
			ctx.foldT = {}
			ctx.statePrev = {}
			ctx.toolPrev = {}
			ctx.compState = {}
			ctx.xdrvPrev = {}
			ctx.wsEma = {}
			ctx.slipProbeDone = false
			ctx.slipProbeDt = 0
		end
		local sa = vehicle.spec_attacherJoints
		if sa ~= nil and type(sa.attacherJoints) == "table" then
			for j, jd in ipairs(sa.attacherJoints) do
				local a = jd ~= nil and jd.moveAlpha or nil
				if finite(a) then
					local prev = ctx.jointAlpha[j]
					ctx.jointAlpha[j] = a
					local jr, jmov = 0, false
					if prev ~= nil and math.abs(a - prev) > 0.0005 then
						moving = true
						cycle = true
						jmov = true
						-- Same stale-prev bound as the fold rate above.
						if dtSec ~= nil and math.abs(a - prev) < 0.25 then
							jr = math.abs(a - prev) / dtSec
							if jr > rate then
								rate = jr
							end
						end
					end
					-- jmov, not jr > 0: a joint whose rate was swallowed by
					-- the stale-delta guard is still moving, and must not
					-- read as a finished stage.
					noteComp("j" .. j, jr, jmov)
				end
			end
		end
	end)
	if unitOn(vehicle) then
		impl = true
	end
	local vk = unitKey(vehicle, 0)
	if unitFolding(vehicle, vk) then
		moving = true
		cycle = true
	end
	if unitPipeMoving(vehicle) then
		moving = true
		cycle = true
		-- Pipe travel has no measurable alpha, so give it a nominal mid
		-- rate (mod 0.2.17): the hum gets a sensible loudness and a touch
		-- of speed pitch instead of reading as rate 0.
		if rate < 0.35 then
			rate = 0.35
		end
	end
	unitStateEvents(vehicle, vk)
	unitToolMotion(vehicle, vk)
	if not ctx.fnProbeDone then
		pcall(function()
			local sp = vehicle.spec_pipe
			local sc = vehicle.spec_combine
			if sp ~= nil or sc ~= nil then
				ctx.fnProbeDone = true
				-- "nil" here means the spec exists but the field moved.
				print(string.format(
					"TF4ALLTelemetry: fn probe: pipe cur=%s tgt=%s swath=%s",
					sp ~= nil and tostring(sp.currentState) or "-",
					sp ~= nil and tostring(sp.targetState) or "-",
					sc ~= nil and tostring(sc.isSwathActive) or "-"))
			end
		end)
	end
	if vehicle.getAttachedImplements ~= nil then
		local o, list = pcall(vehicle.getAttachedImplements, vehicle)
		if o and type(list) == "table" then
			for ai, att in ipairs(list) do
				local u = att ~= nil and att.object or nil
				if u ~= nil then
					if unitOn(u) then
						impl = true
					end
					local uk = unitKey(u, ai)
					if unitFolding(u, uk) then
						moving = true
						cycle = true
					end
					if unitPipeMoving(u) then
						moving = true
						cycle = true
						-- Same nominal rate as the vehicle's own pipe.
						if rate < 0.35 then
							rate = 0.35
						end
					end
					unitStateEvents(u, uk)
					unitToolMotion(u, uk)
					local f = unitFill(u)
					if f > fill then
						fill = f
					end
					if u.getTotalMass ~= nil then
						local oM, m = pcall(u.getTotalMass, u)
						if oM and finite(m) and m > 0 then
							massKg = massKg + m * 1000
						end
					end
				end
			end
		end
	end
	if rate > 5 then
		rate = 5
	end
	-- Stage ends (mod 0.2.22). A part went still while the cycle carries
	-- on, so the machine finished a stage rather than the whole job: bump
	-- the counter and report how fast that part was travelling when it
	-- stopped, which the plugin turns into a momentum-weighted thud. When
	-- NOTHING is still moving the motion simply ended, and the plugin's
	-- own settle already covers it. The cooldown coalesces a sequence of
	-- parts landing together into one thud instead of a burst.
	if ctx.phaseCool > 0 then
		ctx.phaseCool = ctx.phaseCool - (finite(ctx.frameDt) and ctx.frameDt or 0)
	end
	if #stalls > 0 and moving and ctx.phaseCool <= 0 then
		local best = 0
		for _, s in ipairs(stalls) do
			if s > best then
				best = s
			end
		end
		ctx.implPhase = ctx.implPhase + 1
		ctx.implPhSpd = best > 5 and 5 or best
		ctx.phaseCool = 200
	end
	return impl, fill, massKg, moving, rate, moving and not cycle
end

-- The driven vehicle's own propellant tank (mod 0.2.21): level and
-- capacity in the tank's native unit (diesel litres, electric kWh,
-- methane kg) plus the game's own smoothed burn rate (units per hour),
-- so the plugin can derive percent and time remaining for the dash.
-- Checked in the order GIANTS' own HUD uses, first tank that exists
-- wins. Everything through pcall: an electric or methane machine has
-- no DIESEL consumer and getConsumerFillUnitIndex(nil) must skip,
-- never crash the frame.
local FUEL_TYPES = { "DIESEL", "ELECTRICCHARGE", "METHANE" }
local function fuelState(vehicle)
	if vehicle.getConsumerFillUnitIndex == nil
		or vehicle.getFillUnitFillLevel == nil then
		return nil
	end
	for _, tn in ipairs(FUEL_TYPES) do
		local ft = FillType ~= nil and FillType[tn] or nil
		if ft ~= nil then
			local okI, idx = pcall(vehicle.getConsumerFillUnitIndex, vehicle, ft)
			if okI and idx ~= nil then
				local okL, lvl = pcall(vehicle.getFillUnitFillLevel, vehicle, idx)
				if okL and finite(lvl) and lvl >= 0 then
					local cap = nil
					if vehicle.getFillUnitCapacity ~= nil then
						local okC, c = pcall(vehicle.getFillUnitCapacity, vehicle, idx)
						if okC and finite(c) and c > 0 then
							cap = c
						end
					end
					local use = nil
					local sm = vehicle.spec_motorized
					if sm ~= nil and finite(sm.lastFuelUsage) and sm.lastFuelUsage >= 0 then
						use = sm.lastFuelUsage
					end
					return lvl, cap, use, string.lower(tn)
				end
			end
		end
	end
	return nil
end

local function motorLoad01(vehicle)
	if vehicle.getMotor == nil then
		return nil
	end
	local ok, motor = pcall(vehicle.getMotor, vehicle)
	if not ok or motor == nil then
		return nil
	end
	if motor.getSmoothLoadPercentage ~= nil then
		local o, r = pcall(motor.getSmoothLoadPercentage, motor)
		if o and finite(r) then
			return r
		end
	end
	if motor.getMotorAppliedTorque ~= nil and motor.getMotorAvailableTorque ~= nil then
		local o1, applied = pcall(motor.getMotorAppliedTorque, motor)
		local o2, avail = pcall(motor.getMotorAvailableTorque, motor)
		if o1 and o2 and finite(applied) and finite(avail) and avail > 0 then
			return applied / avail
		end
	end
	return nil
end

-- A wheel may carry its physics fields directly (older layouts) or inside a
-- .physics sub-object (newer layouts). Read through either.
local function wheelField(w, name)
	local v = w[name]
	if v == nil and w.physics ~= nil then
		v = w.physics[name]
	end
	return v
end

local function wheelNetInfo(w)
	local ni = w.netInfo
	if ni == nil and w.physics ~= nil then
		ni = w.physics.netInfo
	end
	return ni
end

function TF4ALLTelemetry:buildLine()
	local ok, vehicle = pcall(function() return TF4ALLTelemetry:getCurrentVehicle() end)
	if not ok or vehicle == nil then
		return '{"v":2,"inVehicle":false}'
	end

	local parts = { '"v":2', '"modVer":"' .. MOD_VERSION .. '"', '"inVehicle":true' }

	-- Vehicle identity. SimHub has no car identity for Farming Simulator
	-- (it fabricates a per-session placeholder id), so this pipe is the
	-- only stable source: configFileName is unique per vehicle type and
	-- identical across sessions, saves and machines (mods included), and
	-- getFullName is the official display name the game itself shows.
	-- %q escapes quotes/backslashes, same as the gearName field.
	if type(vehicle.configFileName) == "string" and vehicle.configFileName ~= "" then
		table.insert(parts, '"carCfg":' .. string.format("%q", vehicle.configFileName))
	end
	if vehicle.getFullName ~= nil then
		local oN, nm = pcall(vehicle.getFullName, vehicle)
		if oN and type(nm) == "string" and nm ~= "" then
			table.insert(parts, '"carName":' .. string.format("%q", nm))
		end
	end

	-- Vehicle basics: this pipe is the plugin's PRIMARY telemetry for
	-- Farming Simulator (SimHub's own mod stays its own consumer), so it
	-- carries everything the force feedback and effects need, not just the
	-- physics extras.
	if vehicle.getLastSpeed ~= nil then
		local o, sp = pcall(vehicle.getLastSpeed, vehicle)
		if o and finite(sp) then
			table.insert(parts, string.format('"speedKmh":%.3f', sp))
		end
	end
	if vehicle.getMotor ~= nil then
		local okE, engine = pcall(vehicle.getMotor, vehicle)
		if okE and engine ~= nil then
			if engine.getLastRealMotorRpm ~= nil then
				local o, r = pcall(engine.getLastRealMotorRpm, engine)
				if o and finite(r) then table.insert(parts, string.format('"rpm":%.2f', r)) end
			end
			if engine.getMaxRpm ~= nil then
				local o, r = pcall(engine.getMaxRpm, engine)
				if o and finite(r) then table.insert(parts, string.format('"maxRpm":%.2f', r)) end
			end
			if engine.getMinRpm ~= nil then
				local o, r = pcall(engine.getMinRpm, engine)
				if o and finite(r) then table.insert(parts, string.format('"minRpm":%.2f', r)) end
			end
			if finite(engine.gear) then
				table.insert(parts, string.format('"gear":%d', engine.gear))
			end
		end
	end
	if vehicle.spec_motorized ~= nil then
		local okS, started = pcall(function()
			return vehicle.spec_motorized.isMotorStarted or (vehicle.spec_motorized.motorState ~= 1)
		end)
		if okS then table.insert(parts, '"isMotorStarted":' .. tostring(started == true)) end
		if vehicle.getGearInfoToDisplay ~= nil then
			local okG, gearName, _, _, _, _, _, _, _, isGearChanging = pcall(vehicle.getGearInfoToDisplay, vehicle)
			if okG and type(gearName) == "string" then
				table.insert(parts, '"gearName":' .. string.format("%q", gearName))
			end
			if okG and isGearChanging ~= nil then
				table.insert(parts, '"isGearChanging":' .. tostring(isGearChanging == true))
			end
		end
	end

	local load = motorLoad01(vehicle)
	if load ~= nil then
		table.insert(parts, string.format('"motorLoad":%.4f', load))
	end
	-- Fuel: emitted only while a tank is readable, so absence tells the
	-- plugin "not reported" (older mod, or a vehicle with no consumer).
	local okF, fuelL, fuelCap, fuelUse, fuelT = pcall(fuelState, vehicle)
	if okF and fuelL ~= nil then
		table.insert(parts, string.format('"fuelL":%.2f', fuelL))
		if fuelCap ~= nil then
			table.insert(parts, string.format('"fuelCap":%.1f', fuelCap))
		end
		if fuelUse ~= nil then
			table.insert(parts, string.format('"fuelUse":%.3f', fuelUse))
		end
		if fuelT ~= nil then
			table.insert(parts, '"fuelT":"' .. fuelT .. '"')
		end
	end
	local okI, impl, fill, towKg, implMove, implSpd, implMan = pcall(gatherAttachState, vehicle)
	if okI then
		table.insert(parts, '"impl":' .. tostring(impl == true))
		table.insert(parts, '"implMove":' .. tostring(implMove == true))
		if finite(implSpd) then
			-- Always emitted (0 when idle) so stopping resets the reader.
			table.insert(parts, string.format('"implSpd":%.3f', implSpd))
		end
		table.insert(parts, string.format('"implEvt":%d', ctx.implEvt))
		table.insert(parts, '"implMan":' .. tostring(implMan == true))
		table.insert(parts, string.format('"implPhase":%d,"implPhSpd":%.3f',
			ctx.implPhase, ctx.implPhSpd))
		if finite(fill) then
			table.insert(parts, string.format('"fill":%.3f', fill))
		end
		if finite(towKg) then
			-- Always emitted (0 included) so detaching resets the reader.
			table.insert(parts, string.format('"towKg":%.0f', towKg))
		end
	end

	-- Chassis angular velocity (world frame; Y is yaw for an upright
	-- vehicle). Feeds yaw rate and the derived lateral load plugin-side.
	if vehicle.components ~= nil and vehicle.components[1] ~= nil
		and vehicle.components[1].node ~= nil then
		local okA, ax, ay, az = pcall(getAngularVelocity, vehicle.components[1].node)
		if okA and finite(ax) and finite(ay) and finite(az) then
			table.insert(parts,
				string.format('"avx":%.5f,"avy":%.5f,"avz":%.5f', ax, ay, az))
		end
	end

	local spec = vehicle.spec_wheels
	if spec ~= nil and spec.wheels ~= nil then
		local ws = {}
		for i, w in ipairs(spec.wheels) do
			if i > 8 then
				break
			end
			if not ctx.wheelKeysDumped then
				ctx.wheelKeysDumped = true
				local keys = {}
				for k in pairs(w) do
					table.insert(keys, tostring(k))
				end
				print("TF4ALLTelemetry: wheel[1] fields: " .. table.concat(keys, ", "))
				if type(w.physics) == "table" then
					local pkeys = {}
					for k in pairs(w.physics) do
						table.insert(pkeys, tostring(k))
					end
					print("TF4ALLTelemetry: wheel[1].physics fields: " .. table.concat(pkeys, ", "))
					-- Methods live on the metatable, invisible to pairs over
					-- the instance; dump them once so slip/speed getters are
					-- discoverable from a log file.
					local mt = getmetatable(w.physics)
					local idx = mt ~= nil and mt.__index or nil
					if type(idx) == "table" then
						local mkeys = {}
						for k in pairs(idx) do
							table.insert(mkeys, tostring(k))
						end
						print("TF4ALLTelemetry: wheel[1].physics methods: " .. table.concat(mkeys, ", "))
					end
				end
			end
			local seg = { string.format('"i":%d', i) }
			local ni = wheelNetInfo(w)
			if ni ~= nil and finite(ni.y) then
				table.insert(seg, string.format('"y":%.5f', ni.y))
			end
			local steer = wheelField(w, "steeringAngle")
			if finite(steer) then
				table.insert(seg, string.format('"steer":%.5f', steer))
			end
			local contact = wheelField(w, "hasGroundContact")
			if contact ~= nil then
				table.insert(seg, '"contact":' .. tostring(contact == true))
			end
			-- Per-wheel slip. FS25 no longer caches a lastSlip field on the
			-- wheel (verified against the live physics dump 2026-08-08), so
			-- ask the physics engine directly: getWheelShapeSlip is the
			-- engine call GIANTS' own code uses, taking the wheel's node +
			-- its wheelShape index. lastSlip kept as first choice for FS22.
			local slip = wheelField(w, "lastSlip")
			if not finite(slip) and getWheelShapeSlip ~= nil then
				local ph = w.physics
				local node = w.node or (ph ~= nil and ph.node or nil)
				local shape = ph ~= nil and ph.wheelShape or w.wheelShape
				if node ~= nil and shape ~= nil then
					local okS, s = pcall(getWheelShapeSlip, node, shape)
					if okS and finite(s) then
						slip = s
					end
				end
			end
			if finite(slip) then
				table.insert(seg, string.format('"slip":%.4f', slip))
			end
			-- Derived wheel surface speed (mod 0.2.12): FS25 exposes no
			-- slip anywhere (field dump + method dump both verified), so
			-- compute it: delta of lastXDrive (the mesh-rotation angle)
			-- over the frame gap, times tire radius. SIGNED; the plugin
			-- compares against ground speed: above = wheelspin, below =
			-- lockup, which no GIANTS field ever offered.
			local xd = wheelField(w, "lastXDrive")
			local rad = wheelField(w, "radius")
			if finite(xd) and finite(rad) and finite(ctx.frameDt)
				and ctx.frameDt >= 5 and ctx.frameDt <= 100 then
				local prev = ctx.xdrvPrev[i]
				ctx.xdrvPrev[i] = xd
				if prev ~= nil then
					local d = xd - prev
					-- The angle wraps; a real delta at <=100 ms stays well
					-- under pi, so fold wrap jumps back into [-pi, pi].
					local twoPi = 2 * math.pi
					while d > math.pi do d = d - twoPi end
					while d < -math.pi do d = d + twoPi end
					local wsInst = (d / (ctx.frameDt / 1000)) * rad
					if finite(wsInst) then
						local prevE = ctx.wsEma[i]
						local e = prevE ~= nil
							and (prevE + (wsInst - prevE) * 0.3) or wsInst
						ctx.wsEma[i] = e
						table.insert(seg, string.format('"ws":%.3f', e))
						-- Angular speed too (rad/s, same EMA rescaled):
						-- fills WheelRotRadS for the rev-locked rear pulse.
						if rad > 0.05 then
							table.insert(seg, string.format('"wr":%.3f', e / rad))
						end
					end
				end
			elseif finite(xd) then
				ctx.xdrvPrev[i] = xd
			end
			-- Tire load, for load-weighted slip averaging plugin-side
			-- (the real Forza/AC convention instead of binary contact).
			if w.physics ~= nil and w.physics.getTireLoad ~= nil then
				local okL, l = pcall(w.physics.getTireLoad, w.physics)
				if okL and finite(l) and l >= 0 then
					table.insert(seg, string.format('"load":%.2f', l))
				end
			end
			local px = wheelField(w, "positionX")
			if finite(px) then
				table.insert(seg, string.format('"x":%.4f', px))
			end
			table.insert(ws, "{" .. table.concat(seg, ",") .. "}")
		end
		table.insert(parts, '"wheels":[' .. table.concat(ws, ",") .. "]")
		-- One-shot probe ~5 s into a vehicle: raw numbers for validating
		-- the derived-slip math from a log file.
		if not ctx.slipProbeDone then
			ctx.slipProbeDt = ctx.slipProbeDt + (finite(ctx.frameDt) and ctx.frameDt or 0)
			if ctx.slipProbeDt > 5000 then
				ctx.slipProbeDone = true
				pcall(function()
					local w1 = spec.wheels[1]
					print(string.format(
						"TF4ALLTelemetry: slip probe: xDrive=%s radius=%s speedKmh=%s frameDt=%s",
						tostring(wheelField(w1, "lastXDrive")),
						tostring(wheelField(w1, "radius")),
						tostring(vehicle.getLastSpeed ~= nil and vehicle:getLastSpeed() or "?"),
						tostring(ctx.frameDt)))
					-- Wheel geometry map (one-shot): index -> longitudinal
					-- position + steering angle, to learn the z-sign
					-- convention for true front/rear classification on
					-- rear-steer machines (combines).
					local zs = {}
					for i2, w2 in ipairs(spec.wheels) do
						if i2 > 8 then
							break
						end
						local z2 = wheelField(w2, "positionZ")
						local st2 = wheelField(w2, "steeringAngle")
						table.insert(zs, string.format("%d:z=%s,s=%s", i2,
							finite(z2) and string.format("%.2f", z2) or "?",
							finite(st2) and string.format("%.2f", st2) or "?"))
					end
					print("TF4ALLTelemetry: wheel geometry: " .. table.concat(zs, " "))
				end)
			end
		end
	end

	return "{" .. table.concat(parts, ",") .. "}"
end

function TF4ALLTelemetry:update(dt)
	ctx.updateDt = ctx.updateDt + dt
	if ctx.updateDt < 10 then
		return
	end
	local step = ctx.updateDt
	ctx.updateDt = 0

	-- Reconnect every ~10 s: a SimHub restart leaves this side holding a
	-- dead handle that Lua's io cannot detect (writes report no error), so
	-- a periodic reopen is the only recovery. The plugin's server keeps
	-- SPARE listener instances armed, so the reopen lands on a fresh
	-- instance instantly, no telemetry gap (the old 3 s cadence against a
	-- single-instance server was what flapped; 10 s against a multi-
	-- instance server is seamless and bounds a SimHub-restart outage).
	if ctx.file ~= nil then
		ctx.connAgeDt = ctx.connAgeDt + step
		if ctx.connAgeDt >= 10000 then
			ctx.connAgeDt = 0
			pcall(function() ctx.file:close() end)
			ctx.file = nil
			ctx.reopenDt = 1000   -- reopen on this same tick below
		end
	end
	if ctx.file == nil then
		ctx.reopenDt = ctx.reopenDt + step
		if ctx.reopenDt < 1000 then
			return
		end
		ctx.reopenDt = 0
		local ok, f = pcall(io.open, ctx.pipeName, "w")
		if ok and f ~= nil then
			ctx.file = f
			ctx.connAgeDt = 0
		else
			return
		end
	end

	-- Frame gap for the derived-slip math (ms since the previous build).
	ctx.frameDt = step

	local okB, line = pcall(function() return TF4ALLTelemetry:buildLine() end)
	if not okB then
		return
	end
	-- Write-failure detection that works on Lua 5.1: on SUCCESS write
	-- returns nothing there (newer Luas return the file), so testing the
	-- first return against nil reads every success as a failure and cut
	-- the stream to one frame per reconnect second (live, 2026-08-07).
	-- Failure is nil PLUS an error message; only that pair reconnects.
	local okW, failMsg = pcall(function()
		local r, e = ctx.file:write(line .. "\n")
		if r == nil and e ~= nil then
			return e
		end
		ctx.file:flush()
		return nil
	end)
	if not okW or failMsg ~= nil then
		pcall(function() ctx.file:close() end)
		ctx.file = nil
		ctx.reopenDt = 1000   -- retry promptly after a drop
	end
end

addModEventListener(TF4ALLTelemetry)
