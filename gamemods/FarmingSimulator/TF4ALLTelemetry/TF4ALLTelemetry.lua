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
local ctx = {
	pipeName = "\\\\.\\pipe\\TF4ALLTelemetry",
	file = nil,
	updateDt = 0,
	updateCount = 0,
	wheelKeysDumped = false,
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

	local parts = { '"v":2', '"inVehicle":true' }

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
			local px = wheelField(w, "positionX")
			if finite(px) then
				table.insert(seg, string.format('"x":%.4f', px))
			end
			table.insert(ws, "{" .. table.concat(seg, ",") .. "}")
		end
		table.insert(parts, '"wheels":[' .. table.concat(ws, ",") .. "]")
	end

	return "{" .. table.concat(parts, ",") .. "}"
end

function TF4ALLTelemetry:initPipe()
	-- Same reconnect strategy as SimHub's mod: reopen the pipe periodically
	-- so a plugin restart on the PC side is picked up without game restart.
	if ctx.updateCount == 0 then
		if ctx.file ~= nil then
			pcall(function()
				ctx.file:flush()
				ctx.file:close()
			end)
			ctx.file = nil
		end
		local ok, f = pcall(io.open, ctx.pipeName, "w")
		if ok then
			ctx.file = f
		end
	end
	ctx.updateCount = ctx.updateCount + 1
	if ctx.updateCount == 300 then
		ctx.updateCount = 0
	end
end

function TF4ALLTelemetry:update(dt)
	ctx.updateDt = ctx.updateDt + dt
	if ctx.updateDt < 10 then
		return
	end
	ctx.updateDt = 0

	self:initPipe()
	if ctx.file == nil then
		return
	end

	local ok, line = pcall(function() return TF4ALLTelemetry:buildLine() end)
	if not ok then
		return
	end
	pcall(function()
		ctx.file:write(line .. "\n")
		ctx.file:flush()
	end)
end

addModEventListener(TF4ALLTelemetry)
