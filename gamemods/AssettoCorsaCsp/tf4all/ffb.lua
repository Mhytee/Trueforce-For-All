-- TF4ALL FFB Bridge for Assetto Corsa (Custom Shaders Patch).
--
-- A CSP "ffb-postprocess" script: CSP calls script.update() once per
-- physics frame (333 Hz) with the game's post-processing-chain FFB value.
-- Normally a PURE PASS-THROUGH (it returns ffbValue and ffbDamper
-- unchanged): its job is publishing the game's FFB and per-wheel tire data
-- into a named shared memory section Trueforce For All reads from outside
-- the game. When TF4ALL is actively taking the wheel over (it says so
-- through a second, control shared-memory section, with a liveness
-- heartbeat), this script instead returns 0 to the wheel so the game stops
-- driving it, and TF4ALL renders the exported post-gain ffbValue itself.
-- That keeps the player's in-game gain AND every CSP FFB tweak intact
-- (ffbValue is post-tweak, post-gain) while freeing the wheel's HID pipe so
-- the rev lights and base screen do not fight the game's force. The moment
-- the heartbeat goes stale (SimHub closed), it reverts to pass-through, so
-- the wheel is never left silent.
--
-- Why: vanilla AC shared memory carries only finalFF (the post-gain output
-- signal). CSP additionally exposes steerTorque, ffbPure, ffbMultiplier and
-- per-wheel mz/fx/fy/load, none of which exist in the vanilla pages. Those
-- are the inputs for higher-fidelity AC effects work on the TF4ALL side.
--
-- Rates: the update() arguments and ac.getCarPhysicsRate() are true
-- physics-rate values. ac.getCar() state (ffbPure and friends) refreshes
-- once per GRAPHICS frame even when read from a physics script, so those
-- fields can trail by a few physics frames; consumers should treat them as
-- ~render-rate, which is fine for gain/shape decisions.
--
-- Seqlock: seq is bumped to ODD before the field writes and back to EVEN
-- after, so a reader that sees an odd seq (or a seq that changed across its
-- read) simply re-reads. Same convention CSP's own external-app bridges use.

local LAYOUT = [[
  uint32_t magic;
  uint32_t version;
  uint32_t seq;
  float ffbValue;
  float ffbPure;
  float ffbFinal;
  float ffbMultiplier;
  float steerTorque;
  float steerInput;
  float dt;
  float slipAngle[4];
  float slipRatio[4];
  float ndSlip[4];
  float load[4];
  float mz[4];
  float fx[4];
  float fy[4];
  float ffbDamper;
  float steerInputSpeed;
  uint32_t acLeds;
  uint32_t acLedCount;
  float acLedRpm[12];
  float acLedBlinkRpm;
  float acLedBlinkHz;
  float acLedRgb[36];
]]

local mmf = ac.writeMemoryMappedFile('TF4All.ACBridge.v1', LAYOUT)
mmf.magic   = 0x54463441   -- "TF4A" little-endian ("A4FT" as bytes)
mmf.version = 5                  -- v5: the car's shift-light COLOURS after v4's thresholds
mmf.seq     = 0

-- Physics-rate per-wheel data needs a CSP new enough to expose
-- StateCarPhysicsRate.wheels (present in 0.2.11; absent in some older 0.2.x
-- builds). Feature-detect once; without it the per-wheel fields fall back to
-- the graphics-rate car.wheels values below.
local physRate = nil
pcall(function()
  local pr = ac.getCarPhysicsRate()
  if pr ~= nil and pr.wheels ~= nil then physRate = pr end
end)

-- Who owns the wheel's rev lights. In AC that is CSP's g27_lights module,
-- and TF4ALL needs to know because both of them writing to the same HID++
-- pipe is what makes the bar stick and lag. CSP exposes no way to suppress
-- the module's output at runtime, so this is strictly a REPORT: TF4ALL uses
-- it to decide whether to stay off the bar and what to tell the user.
--
-- Packed into one word so the layout grows by a single field: byte 0 is
-- whether the module is active at all, byte 1 is its MODE (the config value
-- that decides whether it writes, DISABLED being the one that frees the bar
-- for a "specialized tool" per CSP's own description of the setting).
local LED_MODES = { DI_BASED = 1, PERCENTAGE = 2, AI_BASED = 3, DISABLED = 4 }
local acLedsWord = 0

local function refreshLedState()
  local active, mode = false, 0
  -- pcall throughout: ac.isModuleActive and ac.INIConfig.cspModule are both
  -- newer than the oldest CSP this script runs on, and an older patch must
  -- fall back to "unknown" (0) rather than take the FFB bridge down with it.
  pcall(function() active = ac.isModuleActive(ac.CSPModuleID.G27Lights) == true end)
  pcall(function()
    local cfg = ac.INIConfig.cspModule(ac.CSPModuleID.G27Lights)
    if cfg ~= nil then
      mode = LED_MODES[tostring(cfg:get('BASIC', 'MODE', 'DI_BASED'))] or 0
    end
  end)
  acLedsWord = (active and 1 or 0) + mode * 256
end

refreshLedState()
-- Re-read when the user changes it, so turning AC's lights off mid-session
-- reaches TF4ALL without a restart.
pcall(function() ac.onCSPConfigChanged(ac.CSPModuleID.G27Lights, refreshLedState) end)

-- The CAR's own shift lights, so the wheel's bar can light where the car's
-- dash lights instead of at a generic percentage of the rev range.
--
-- AC cars describe their dash LEDs in data/digital_instruments.ini as
-- [LED_0], [LED_1], ... each with the RPM it switches on at, plus the RPM the
-- set starts flashing at and how fast. Optional data: plenty of cars model no
-- shift lights at all, and those report a count of zero so TF4ALL keeps
-- whatever it would have done anyway.
--
-- ac.INIConfig.carData reads this straight out of data.acd, so it works for
-- the packed cars that are almost all of them. Doing the same from outside the
-- game would mean implementing AC's own container format.
--
-- Read once: CSP loads this script per session, and the car does not change
-- under it within one.
local LED_MAX = 12
local acLedCount, acLedBlinkRpm, acLedBlinkHz = 0, 0, 0
local acLedRpm = {}
-- EMISSIVE per LED, raw. AC treats these as emissive intensities rather than
-- 0-255 colours (values above 255 are normal, e.g. COLOR=450,70,10 elsewhere
-- in the same file), so they are published UNSCALED and normalised on the
-- TF4ALL side where the rule can be tested.
local acLedRgb = {}

local function readCarShiftLights()
  pcall(function()
    local cfg = ac.INIConfig.carData(0, 'digital_instruments.ini')
    if cfg == nil then return end
    for i = 0, LED_MAX - 1 do
      -- The default's TYPE is what INIConfig:get returns on a miss, so -1
      -- keeps this numeric and makes "absent" unambiguous.
      local rpm = cfg:get('LED_' .. i, 'RPM_SWITCH', -1)
      if type(rpm) ~= 'number' or rpm <= 0 then break end   -- LEDs are contiguous from 0
      acLedCount = acLedCount + 1
      acLedRpm[acLedCount] = rpm

      -- Its own pcall, and its own default: rgb is a CSP type rather than a
      -- plain table, so probing it defensively here would be guesswork, and an
      -- error raised inside the shared pcall above would lose every LED after
      -- this one. An all-zero triple reads as "no colour" downstream, which is
      -- the right answer for a car that does not give one.
      local base = (acLedCount - 1) * 3
      acLedRgb[base + 1], acLedRgb[base + 2], acLedRgb[base + 3] = 0, 0, 0
      pcall(function()
        local col = cfg:get('LED_' .. i, 'EMISSIVE', rgb(0, 0, 0))
        acLedRgb[base + 1] = col.r or 0
        acLedRgb[base + 2] = col.g or 0
        acLedRgb[base + 3] = col.b or 0
      end)
      -- Every LED repeats the same blink pair; take the first that has it.
      if acLedBlinkRpm <= 0 then
        local b = cfg:get('LED_' .. i, 'BLINK_SWITCH', -1)
        if type(b) == 'number' and b > 0 then acLedBlinkRpm = b end
      end
      if acLedBlinkHz <= 0 then
        local h = cfg:get('LED_' .. i, 'BLINK_HZ', -1)
        if type(h) == 'number' and h > 0 then acLedBlinkHz = h end
      end
    end
  end)
end

readCarShiftLights()

-- TF4ALL -> script control channel. TF4ALL creates and writes this; we only
-- read it. seq advances by two per write and doubles as a liveness heartbeat.
-- We open it lazily (TF4ALL may start after AC) and fall back to pass-through
-- whenever it is missing, its header is wrong, or its writes go stale.
local CONTROL_LAYOUT = [[
  uint32_t magic;
  uint32_t version;
  uint32_t seq;
  uint32_t flags;
]]
local CONTROL_MAGIC = 0x54464331   -- "TFC1"
local ctrl = nil
local ctrlRetry = 0
local ctrlPrevSeq = -1
local ctrlSinceChange = 1e9        -- seconds since seq last advanced
local ctrlSuppress = false
local ctrlDampScale = 1         -- last decoded suppress flag

local function readControl(dt)
  if ctrl == nil then
    ctrlRetry = ctrlRetry - dt
    if ctrlRetry > 0 then return false end
    ctrlRetry = 1.0
    pcall(function() ctrl = ac.readMemoryMappedFile('TF4All.ACBridge.Control.v1', CONTROL_LAYOUT) end)
    if ctrl == nil then return false end
  end
  local ok = false
  local scale = 1
  pcall(function()
    if ctrl.magic ~= CONTROL_MAGIC or ctrl.version ~= 1 then return end
    local seq = ctrl.seq
    if seq % 2 ~= 0 then                -- writer mid-write: keep the last decision
      ok = ctrlSuppress
      scale = ctrlDampScale
      return
    end
    if seq ~= ctrlPrevSeq then
      ctrlPrevSeq = seq
      ctrlSinceChange = 0
    else
      ctrlSinceChange = ctrlSinceChange + dt
    end
    -- Stale writes = SimHub gone: revert to pass-through.
    if ctrlSinceChange > 0.5 then ok = false
    else
      -- tonumber: ctrl.flags is FFI cdata, and math.floor on cdata throws
      -- (silently, inside this pcall), which ate the damper bits entirely.
      local flags = tonumber(ctrl.flags) or 0
      ok = (flags % 2) == 1                          -- bit 0: take the wheel over
      if (math.floor(flags / 4) % 2) == 1 then       -- bit 2: scale override active,
        scale = (math.floor(flags / 256) % 256) / 255 -- bits 8..15 carry the scale
      elseif (math.floor(flags / 2) % 2) == 1 then   -- bit 1: A/B, damper off
        scale = 0
      else
        scale = 1
      end
    end
  end)
  ctrlSuppress = ok
  ctrlDampScale = scale
  return ok, scale
end

function script.update(ffbValue, ffbDamper, steerInput, steerInputSpeed, dt)
  local s = mmf.seq + 1
  mmf.seq = s                      -- odd: writer busy

  mmf.ffbValue        = ffbValue
  mmf.ffbDamper       = ffbDamper
  mmf.steerInput      = steerInput
  mmf.steerInputSpeed = steerInputSpeed
  mmf.dt              = dt
  mmf.acLeds          = acLedsWord
  -- Constant for the session, but written inside the seqlock with everything
  -- else so a reader that attaches late still gets them without a handshake.
  mmf.acLedCount      = acLedCount
  mmf.acLedBlinkRpm   = acLedBlinkRpm
  mmf.acLedBlinkHz    = acLedBlinkHz
  for i = 0, LED_MAX - 1 do
    mmf.acLedRpm[i] = acLedRpm[i + 1] or 0
    mmf.acLedRgb[i * 3 + 0] = acLedRgb[i * 3 + 1] or 0
    mmf.acLedRgb[i * 3 + 1] = acLedRgb[i * 3 + 2] or 0
    mmf.acLedRgb[i * 3 + 2] = acLedRgb[i * 3 + 3] or 0
  end

  local car = ac.getCar(0)
  if car ~= nil then
    mmf.ffbPure       = car.ffbPure or 0
    mmf.ffbFinal      = car.ffbFinal or 0
    mmf.ffbMultiplier = car.ffbMultiplier or 1
    mmf.steerTorque   = car.steerTorque or 0
  end

  if physRate ~= nil then
    for i = 0, 3 do
      local w = physRate.wheels[i]
      mmf.slipAngle[i] = w.slipAngle    -- RADIANS on this struct
      mmf.slipRatio[i] = w.slipRatio
      mmf.ndSlip[i]    = w.ndSlip
      mmf.load[i]      = w.load
      mmf.mz[i]        = w.mz
      mmf.fx[i]        = w.fx
      mmf.fy[i]        = w.fy
    end
  elseif car ~= nil then
    for i = 0, 3 do
      local w = car.wheels[i]
      mmf.slipAngle[i] = math.rad(w.slipAngle)   -- DEGREES on StateWheel; normalize to radians
      mmf.slipRatio[i] = w.slipRatio
      mmf.ndSlip[i]    = w.ndSlip
      mmf.load[i]      = w.load
      mmf.mz[i]        = w.mz
      mmf.fx[i]        = w.fx
      mmf.fy[i]        = w.fy
    end
  end

  mmf.seq = s + 1                  -- even: stable

  -- If TF4ALL is taking the wheel over, zero the FORCE so the game stops
  -- driving the wheel (TF4ALL renders the exported ffbValue) but keep the
  -- game's DAMPER: AC holds it at a constant level (the player's damper
  -- gain), the wheel firmware runs it locally off its own encoder, and
  -- without it a released wheel oscillates and full lock arrives hard.
  -- Otherwise pass everything through untouched.
  local takeover, dampScale = readControl(dt)
  if takeover then
    -- dampScale is the CSPFFB DAMP / DAMPTEST lever: 1 = pass the game's
    -- damper through (normal), 0 = the pre-fix feel, in between = the
    -- DAMPTEST ramps, proving on the wheel what the channel does.
    return 0, ffbDamper * dampScale
  end
  return ffbValue, ffbDamper
end
